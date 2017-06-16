﻿using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.VCProjectEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodeAtlasVSIX
{
    public class SolutionTraverser
    {
        public void Traverse()
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte == null)
            {
                return;
            }
            
            Solution solution = dte.Solution;
            TraverseSolution(solution);
        }

        void TraverseSolution(Solution solution)
        {
            if (solution == null)
            {
                return;
            }

            if(BeforeTraverseSolution(solution))
            {
                string solutionFile = solution.FileName;
                Projects projectList = solution.Projects;
                int projectCount = projectList.Count;
                foreach (var proj in projectList)
                {
                    var project = proj as Project;
                    if (project != null)
                    {
                        TraverseProject(project);
                    }
                }
                AfterTraverseSolution(solution);
            }
        }

        void TraverseProject(Project project)
        {
            if (project == null)
            {
                return;
            }

            if(BeforeTraverseProject(project))
            {
                ProjectItems projectItems = project.ProjectItems;
                //var codeModel = project.CodeModel;
                //var codeLanguage = codeModel.Language;

                var items = projectItems.GetEnumerator();
                while (items.MoveNext())
                {
                    var item = items.Current as ProjectItem;
                    TraverseProjectItem(item);
                }
                AfterTraverseProject(project);
            }

        }

        void TraverseProjectItem(ProjectItem item)
        {
            if (item == null)
            {
                return;
            }

            if (BeforeTraverseProjectItem(item))
            {
                if (item.SubProject != null)
                {
                    TraverseProject(item.SubProject);
                }
                var projectItems = item.ProjectItems;
                if (projectItems != null)
                {
                    var items = projectItems.GetEnumerator();
                    while (items.MoveNext())
                    {
                        var currentItem = items.Current as ProjectItem;
                        TraverseProjectItem(currentItem);
                    }
                }
                AfterTraverseProjectItem(item);
            }
        }

        protected virtual bool BeforeTraverseSolution(Solution solution) { return true; }
        protected virtual bool BeforeTraverseProject(Project project) { return true; }
        protected virtual bool BeforeTraverseProjectItem(ProjectItem item) { return true; }

        protected virtual void AfterTraverseSolution(Solution solution) { }
        protected virtual void AfterTraverseProject(Project project) { }
        protected virtual void AfterTraverseProjectItem(ProjectItem item) { }
    }

    public class ProjectFileCollector : SolutionTraverser
    {
        class PathNode
        {
            public PathNode(string name) { m_name = name; }
            public string m_name;
            public Dictionary<string, PathNode> m_children = new Dictionary<string, PathNode>();
        }

        class ProjectInfo
        {
            public HashSet<string> m_includePath;
            public HashSet<string> m_defines;
        }

        string m_solutionName = "";
        string m_solutionPath = "";
        List<string> m_fileList = new List<string>();
        HashSet<string> m_directoryList = new HashSet<string>();
        PathNode m_rootNode = new PathNode("root");
        // Dictionary<string, HashSet<string>> m_projectIncludePath = new Dictionary<string, HashSet<string>>();
        Dictionary<string, ProjectInfo> m_projectInfo = new Dictionary<string, ProjectInfo>();
        List<string> m_extensionList = new List<string> {
            ".c", ".cc", ".cxx", ".cpp", ".c++", ".inl",".h", ".hh", ".hxx", ".hpp", ".h++",".inc", 
            ".java", ".ii", ".ixx", ".ipp", ".i++", ".idl", ".ddl", ".odl",
            ".cs",
            ".d", ".php", ".php4", ".php5", ".phtml", ".m", ".markdown", ".md", ".mm", ".dox",
            ".py",
            ".f90", ".f", ".for",
            ".tcl",
            ".vhd", ".vhdl", ".ucf", ".qsf",
            ".as", ".js" };

        public ProjectFileCollector()
        {
        }

        public List<string> GetDirectoryList()
        {
            return m_directoryList.ToList();
        }

        public List<string> GetAllIncludePath()
        {
            var res = new HashSet<string>();
            foreach (var projectPair in m_projectInfo)
            {
                var includeList = projectPair.Value.m_includePath.ToList();
                foreach (var include in includeList)
                {
                    res.Add(include);
                }
            }
            return res.ToList();
        }

        public List<string> GetAllDefines()
        {
            var res = new HashSet<string>();
            foreach (var projectPair in m_projectInfo)
            {
                var defineList = projectPair.Value.m_defines.ToList();
                foreach (var define in defineList)
                {
                    res.Add(define);
                }
            }
            return res.ToList();
        }

        public string GetSolutionPath()
        {
            return m_solutionPath;
        }

        public string GetSolutionFolder()
        {
            if (m_solutionPath == "")
            {
                return "";
            }
            return System.IO.Path.GetDirectoryName(m_solutionPath).Replace('\\','/');
        }

        public string GetSolutionName()
        {
            return m_solutionName;
        }

        protected override bool BeforeTraverseSolution(Solution solution)
        {
            m_solutionPath = solution.FileName;
            if (m_solutionPath != "")
            {
                m_solutionName = System.IO.Path.GetFileNameWithoutExtension(m_solutionPath);
            }
            return true;
        }

        protected override bool BeforeTraverseProject(Project project)
        {
            Logger.WriteLine("projectname: " + project.Name);
            //var propertyIter = project.Properties.GetEnumerator();
            //while (propertyIter.MoveNext() && false)
            //{
            //    var item = propertyIter.Current as Property;
            //    if (item == null)
            //    {
            //        continue;
            //    }

            //    string propName = item.Name;
            //    string propValue = "";
            //    try
            //    {
            //        propValue = item.Value.ToString();
            //    }
            //    catch
            //    {

            //    }
            //    Logger.WriteLine("   " + propName + ":" + propValue);
            //}
            try
            {
                var configMgr = project.ConfigurationManager;
                var config = configMgr.ActiveConfiguration as Configuration;

                var vcProject = project.Object as VCProject;
                if (vcProject != null)
                {
                    var vccon = vcProject.ActiveConfiguration as VCConfiguration;
                    IVCRulePropertyStorage generalRule = vccon.Rules.Item("ConfigurationDirectories");
                    IVCRulePropertyStorage cppRule = vccon.Rules.Item("CL");

                    // Parsing include path
                    string addIncPath = cppRule.GetEvaluatedPropertyValue("AdditionalIncludeDirectories");
                    string incPath = generalRule.GetEvaluatedPropertyValue("IncludePath");
                    string allIncPath = incPath + ";" + addIncPath;
                    string[] pathList = allIncPath.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var projectInc = new HashSet<string>();
                    var projectPath = Path.GetDirectoryName(project.FileName);
                    foreach (var item in pathList)
                    {
                        string path = item.Trim();
                        if (!path.Contains(":"))
                        {
                            // relative path
                            path = Path.Combine(projectPath, path);
                            path = Path.GetFullPath((new Uri(path)).LocalPath);
                        }
                        if (!Directory.Exists(path))
                        {
                            continue;
                        }
                        path = path.Replace('\\', '/').Trim();
                        projectInc.Add(path);
                        Logger.WriteLine("include path:" + path);
                    }
                    var projInfo = FindProjectInfo(project.Name);
                    projInfo.m_includePath = projectInc;

                    // Parsing define
                    string defines = cppRule.GetEvaluatedPropertyValue("PreprocessorDefinitions");
                    string[] defineList = defines.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var defineSet = new HashSet<string>();
                    foreach (var item in defineList)
                    {
                        defineSet.Add(item);
                    }
                    projInfo.m_defines = defineSet;
                }

                //foreach (VCConfiguration vccon in vcProject.Configurations)
                //{
                //    string ruleStr = "ConfigurationDirectories";
                //    IVCRulePropertyStorage generalRule = vccon.Rules.Item(ruleStr);
                //    IVCRulePropertyStorage cppRule = vccon.Rules.Item("CL");

                //    string addIncPath = cppRule.GetEvaluatedPropertyValue("AdditionalIncludeDirectories");

                //    string incPath = generalRule.GetEvaluatedPropertyValue("IncludePath");
                //    string outputPath = vccon.OutputDirectory;

                //    //vccon.OutputDirectory = "$(test)";
                //    //string test1 = generalRule.GetEvaluatedPropertyValue(2);
                //    //string incPath = generalRule.GetEvaluatedPropertyValue("IncludePath");
                //    //string name = generalRule.GetEvaluatedPropertyValue("TargetName");
                //    Logger.WriteLine("include path:" + incPath);
                //}

                //dynamic propertySheet = vcConfig.PropertySheets;
                //IVCCollection propertySheetCollection = propertySheet as IVCCollection;
                //foreach (var item in propertySheetCollection)
                //{
                //    var vcPropertySheet = item as VCPropertySheet;
                //    if (vcPropertySheet != null)
                //    {
                //        foreach(var rule in vcPropertySheet.Rules)
                //        {
                //            var vcRule = rule as IVCRulePropertyStorage;
                //            if (vcRule != null)
                //            {
                //                vcRule.GetEvaluatedPropertyValue()
                //            }
                //        }
                //    }
                //}
                //var config = vcProject.ActiveConfiguration;
                //if (config != null)
                //{
                //    var configProps = config.Properties;
                //    var configPropIter = configProps.GetEnumerator();
                //    while (configPropIter.MoveNext())
                //    {
                //        var configProp = configPropIter.Current as Property;
                //        var configName = configProp.Name;
                //        var configVal = "";
                //        try
                //        {
                //            configVal = configProp.Value.ToString();
                //        }
                //        catch
                //        {
                //        }
                //        Logger.WriteLine("  " + configName + ":" + configVal);
                //    }

                //    //Logger.WriteLine("group----------------------------");
                //    //var groups = config.OutputGroups;
                //    //var groupIter = groups.GetEnumerator();
                //    //while (groupIter.MoveNext())
                //    //{
                //    //    var group = groupIter.Current as OutputGroup;
                //    //    group.
                //    //}
                //}
            }
            catch
            {
                Logger.WriteLine("project error-------------");
            }
            return true;
        }

        ProjectInfo FindProjectInfo(string name)
        {
            if (!m_projectInfo.ContainsKey(name))
            {
                m_projectInfo[name] = new ProjectInfo();
            }
            return m_projectInfo[name];
        }

        protected override bool BeforeTraverseProjectItem(ProjectItem item)
        {
            string itemName = item.Name;
            string itemKind = item.Kind;
            if (itemKind == Constants.vsProjectItemKindPhysicalFolder)
            {

            }
            else if (itemKind == Constants.vsProjectItemKindPhysicalFile)
            {
                for (short i = 0; i < item.FileCount; i++)
                {
                    string fileName = item.FileNames[i];
                    m_fileList.Add(fileName);
                    //Logger.WriteLine(fileName);

                    //var pathComponents = fileName.Split(new char[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    //PathNode node = m_rootNode;
                    //foreach (var pathComp in pathComponents)
                    //{
                    //    if (!node.m_children.ContainsKey(pathComp))
                    //    {
                    //        node.m_children[pathComp] = new PathNode(pathComp);
                    //    }
                    //    node = node.m_children[pathComp];
                    //}

                    var ext = System.IO.Path.GetExtension(fileName).ToLower();
                    foreach (var extension in m_extensionList)
                    {
                        if (ext == extension)
                        {
                            var directory = System.IO.Path.GetDirectoryName(fileName);
                            directory = directory.Replace('\\', '/');
                            m_directoryList.Add(directory);
                            break;
                        }
                    }
                }
            }
            return true;
        }
    }
}

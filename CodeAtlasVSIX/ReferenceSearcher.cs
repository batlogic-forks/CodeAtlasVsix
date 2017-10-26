﻿using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CodeAtlasVSIX
{
    class ReferenceSearcher
    {
        // Data for find reference
        IVsObjectList m_searchResultList;
        Dictionary<string, string> m_referenceDict = new Dictionary<string, string>(); // Ref
        Dictionary<string, string> m_itemDict = new Dictionary<string, string>(); // ItemName
        string m_srcDocumentPath = "";
        int m_srcLine = 0;
        int m_srcColumn = 0;
        bool m_isFindingReference = false;
        Package m_package;
        int m_count = 0;
        int m_maxCount = 4;

        string m_srcUniqueName = "";
        string m_srcLongName = "";

        public ReferenceSearcher()
        {
        }

        public void SetPackage(Package package)
        {
            m_package = package;
        }

        public bool BeginNewSearch()
        {
            var scene = UIManager.Instance().GetScene();
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            m_searchResultList = null;
            m_referenceDict.Clear();
            m_itemDict.Clear();
            m_srcUniqueName = "";
            m_srcLongName = "";

            var selectedNodes = scene.SelectedNodes();
            if (selectedNodes.Count == 0)
            {
                return false;
            }

            CodeUIItem node = selectedNodes[0];
            m_srcUniqueName = node.GetUniqueName();
            m_srcLongName = node.GetLongName();

            bool res = LaunchSearch(node.GetLongName());
            if (!res)
            {
                res = LaunchSearch(node.GetName());
            }
            m_count = 0;
            return res;
        }

        bool LaunchSearch(string name)
        {
            IVsObjectSearch objectSearch = ((System.IServiceProvider)m_package).GetService(typeof(SVsObjectSearch)) as IVsObjectSearch;
            if (objectSearch == null)
            {
                return false;
            }

            const __VSOBSEARCHFLAGS flags = __VSOBSEARCHFLAGS.VSOSF_EXPANDREFS;
            VSOBSEARCHCRITERIA[] pobSrch = new VSOBSEARCHCRITERIA[1];
            pobSrch[0].grfOptions = (uint)(_VSOBSEARCHOPTIONS.VSOBSO_CASESENSITIVE | _VSOBSEARCHOPTIONS.VSOBSO_LOOKINREFS);
            pobSrch[0].eSrchType = VSOBSEARCHTYPE.SO_ENTIREWORD;
            pobSrch[0].szName = name;

            try
            {
                ErrorHandler.ThrowOnFailure(objectSearch.Find((uint)flags, pobSrch, out m_searchResultList));
                var objectList = m_searchResultList as IVsObjectList2;
                uint resultCount = 0;
                if (objectList.GetItemCount(out resultCount) != VSConstants.S_OK || resultCount <= 0)
                {
                    return false;
                }
                if (resultCount > 0)
                {
                    string text;
                    ushort img;
                    bool isProcessing;
                    GetListItemInfo(objectList, 0, out text, out img, out isProcessing);
                    if (text == "Search found no results")
                    {
                        return false;
                    }
                }
            }
            catch (InvalidCastException)
            {
                return false;
            }
            return true;
        }

        void ProcessReferenceList(out bool isComplete)
        {
            isComplete = false;
            var db = DBManager.Instance().GetDB();
            var objectList = m_searchResultList as IVsObjectList2;
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (objectList == null || dte == null)
            {
                return;
            }

            try
            {
                uint pCount;
                bool isProcessing = false;
                List<DoxygenDB.Entity> targetEntityList = new List<DoxygenDB.Entity>();

                if (objectList.GetItemCount(out pCount) != VSConstants.S_OK)
                {
                    isProcessing = true;
                    return;
                }

                var beginTime = DateTime.Now;
                ushort img;
                bool isDoing;
                string itemText;
                bool longNameMatched = false;
                for (uint i = 0; i < pCount; i++)
                {
                    GetListItemInfo(objectList, i, out itemText, out img, out isDoing);
                    if (itemText.Contains(m_srcLongName))
                    {
                        longNameMatched = true;
                    }
                }

                for (uint i = 0; i < pCount; i++)
                {
                    GetListItemInfo(objectList, i, out itemText, out img, out isDoing);
                    Logger.Debug("+++++++" + itemText);
                    if (longNameMatched && !itemText.Contains(m_srcLongName))
                    {
                        continue;
                    }

                    isProcessing |= isDoing;
                    if (isDoing)
                    {
                        continue;
                    }
                    if (m_itemDict.ContainsKey(itemText))
                    {
                        continue;
                    }

                    IVsObjectList2 subList;
                    bool isItemProcessing = false;
                    if (objectList.GetList2(i, (uint)_LIB_LISTTYPE.LLT_REFERENCES, (uint)(_LIB_LISTFLAGS.LLF_NONE), new VSOBSEARCHCRITERIA2[0], out subList) == VSConstants.S_OK)
                    {
                        // Switch to using our "safe" PInvoke interface for IVsObjectList2 to avoid potential memory management issues
                        // when receiving strings as out params.
                        uint list2ItemCount = 0;
                        if (subList.GetItemCount(out list2ItemCount) != VSConstants.S_OK)
                        {
                            isItemProcessing = true;
                            continue;
                        }

                        for (uint j = 0; j < list2ItemCount; j++)
                        {
                            string text;
                            GetListItemInfo(subList, j, out text, out img, out isDoing);
                            isItemProcessing |= isDoing;
                            if (isDoing || m_referenceDict.ContainsKey(text))
                            {
                                continue;
                            }

                            bool isIgnored = (img == 12 || img == 5);
                            Logger.Debug("Type:" + img + ": " + !isIgnored + ": " + text);

                            // Ignore several reference types
                            // 12: comment
                            if (isIgnored)
                            {
                                continue;
                            }

                            int isOK;
                            int res = subList.CanGoToSource(j, VSOBJGOTOSRCTYPE.GS_REFERENCE, out isOK);
                            if (res != VSConstants.S_OK || isOK == 0)
                            {
                                isItemProcessing = true;
                                continue;
                            }
                            if (subList.GoToSource(j, VSOBJGOTOSRCTYPE.GS_REFERENCE) != VSConstants.S_OK)
                            {
                                Logger.Debug("Go to source failed. " + text);
                                isItemProcessing = true;
                                continue;
                            }

                            ConnectTargetToSource();
                            m_referenceDict[text] = "";

                            var duration = (DateTime.Now - beginTime).TotalMilliseconds;
                            if (duration > 3000)
                            {
                                isItemProcessing = true;
                                return;
                            }
                        }
                    }
                    if (!isItemProcessing)
                    {
                        m_itemDict[itemText] = "";
                    }
                    isProcessing |= isItemProcessing;
                }

                if (!isProcessing)
                {
                    isComplete = true;  // No more item
                }
                Logger.Debug("=========================================");
            }
            catch (InvalidCastException)
            {
                // fixed in VS2010
                // VSBug : trying to cast IVsObjectList2 to IVsObjectList, shows 'Find Results' pane, but pplist is null
            }
        }

        public void UpdateResult()
        {
            if (m_srcUniqueName == "" || m_isFindingReference || m_searchResultList == null || m_count > m_maxCount)
            {
                return;
            }
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte == null)
            {
                return;
            }

            m_isFindingReference = true;

            // Record current status
            Document currentDoc = dte.ActiveDocument;
            var srcSelection = currentDoc.Selection as EnvDTE.TextSelection;
            string srcPath = currentDoc.FullName;
            int srcLine = srcSelection.CurrentLine;
            int srcColumn = srcSelection.CurrentColumn;

            var scene = UIManager.Instance().GetScene();
            var selectedNodes = scene.SelectedNodes();
            var selectedUniqueName = "";
            if (selectedNodes.Count > 0)
            {
                selectedUniqueName = selectedNodes[0].GetUniqueName();
            }

            // Process
            bool isComplete;
            ProcessReferenceList(out isComplete);
            if (isComplete)
            {
                m_searchResultList = null; // no more result
            }

            // Restore status
            scene.AcquireLock();
            scene.SelectCodeItem(selectedUniqueName);
            scene.ReleaseLock();
            GoToDocument(dte, srcPath, srcLine, srcColumn);
            m_count++;
            if (isComplete)
            {
                Logger.Info("Search Completed.");
            }
            else if (m_count > m_maxCount)
            {
                Logger.Info("Search hasn't completed because max count is reached. Please try again.");
            }

            m_isFindingReference = false;
        }

        void ConnectTargetToSource()
        {
            // Get code element under cursor
            //Document doc = null;
            //doc = dte.ActiveDocument;
            //if (doc != null)
            //{
            //    EnvDTE.TextSelection ts = doc.Selection as EnvDTE.TextSelection;
            //    int lineNum = ts.AnchorPoint.Line;
            //    int lineOffset = ts.AnchorPoint.LineCharOffset;
            //    ts.MoveToLineAndOffset(lineNum, lineOffset);
            //}

            // Get symbol name
            CodeElement tarElement;
            Document tarDocument;
            int tarLine;
            CursorNavigator.GetCursorElement(out tarDocument, out tarElement, out tarLine);
            if (tarElement == null)
            {
                Logger.Debug("   Go to source fail. No target element.");
                return;
            }

            // Search entity
            var tarName = tarElement.Name;
            var tarLongName = tarElement.FullName;
            var tarType = CursorNavigator.VSElementTypeToString(tarElement);
            var tarFile = tarDocument.FullName;

            var db = DBManager.Instance().GetDB();
            var tarRequest = new DoxygenDB.EntitySearchRequest(
                tarName, (int)DoxygenDB.SearchOption.MATCH_CASE | (int)DoxygenDB.SearchOption.MATCH_WORD,
                tarLongName, (int)DoxygenDB.SearchOption.MATCH_CASE | (int)DoxygenDB.SearchOption.WORD_CONTAINS_DB | (int)DoxygenDB.SearchOption.DB_CONTAINS_WORD,
                tarType, tarFile, tarLine);
            var tarResult = new DoxygenDB.EntitySearchResult();
            db.SearchAndFilter(tarRequest, out tarResult);

            if (tarResult.bestEntity == null)
            {
                tarRequest.m_longName = "";
                db.SearchAndFilter(tarRequest, out tarResult);
            }

            // Connect custom edge
            if (tarResult.bestEntity != null)
            {
                var scene = UIManager.Instance().GetScene();
                var targetEntity = tarResult.bestEntity;
                scene.AcquireLock();
                scene.AddCodeItem(targetEntity.m_id);
                scene.AddCodeItem(m_srcUniqueName);
                bool res = scene.DoAddCustomEdge(targetEntity.m_id, m_srcUniqueName);
                scene.ReleaseLock();
                Logger.Debug("   Add edge:" + res);
            }
            //var tarResult = DoShowInAtlas();
            //if (tarResult != null && tarResult.bestEntity != null)
            //{
            //    targetEntityList.Add(tarResult.bestEntity);
            //}
        }

        void GetListItemInfo(IVsObjectList2 subList, uint j, out string text, out ushort image, out bool isProcessing)
        {
            // Get Text
            IVsNavInfoNode navInfoNode;
            subList.GetNavInfoNode(j, out navInfoNode);
            navInfoNode.get_Name(out text);
            if (text == null)
            {
                text = "";
            }

            // Get icon, indicating reference type
            VSTREEDISPLAYDATA[] displayData = new VSTREEDISPLAYDATA[1];
            subList.GetDisplayData(j, displayData);

            isProcessing = false;
            isProcessing |= text.Contains("Please Wait...");
            isProcessing |= text.Contains("% of items left to process");

            image = displayData[0].Image;
        }
        
        bool GoToDocument(DTE2 dte, string path, int line, int column)
        {
            Document srcDocument = null;
            if (dte.get_IsOpenFile(EnvDTE.Constants.vsViewKindCode, path))
            {
                srcDocument = dte.Documents.Item(path);
                srcDocument.Activate();
            }
            else
            {
                var window = dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindCode);
                if (window != null)
                {
                    window.Visible = true;
                    window.Activate();
                    srcDocument = window.Document;
                }
            }

            if (srcDocument != null)
            {
                var srcSelection = srcDocument.Selection as EnvDTE.TextSelection;
                if (srcSelection != null)
                {
                    srcSelection.MoveToDisplayColumn(line, column);
                    return true;
                }
            }
            return false;
        }
    }
}

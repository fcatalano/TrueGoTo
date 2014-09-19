﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Careerbuilder.TrueGoTo
{
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists)]
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(GuidList.guidTrueGoToPkgString)]
    public sealed class TrueGoToPackage : Package, IVsSolutionEvents
    {
        private DTE2 _DTE;
        private object _solutionElementsLock;
        private CodeModelEvents _codeEvents;
        private UInt32 _solutionEventsCookie;
        private bool _isInitialized;
        private int _loadedProjectCount;
        private int _maxProjectCount;
        private List<CodeElement> _solutionElements;
        private readonly vsCMElement[] _blackList = new vsCMElement[] { vsCMElement.vsCMElementImportStmt, vsCMElement.vsCMElementUsingStmt, vsCMElement.vsCMElementAttribute };

        public TrueGoToPackage() 
        {
            _solutionElements = new List<CodeElement>();
            _solutionElementsLock = new object();
            _isInitialized = false;
        }

        protected override void Initialize()
        {
            base.Initialize();
            _DTE = (DTE2)GetService(typeof(DTE));
            AddHandlers();
            OleMenuCommandService mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (null != mcs)
            {
                CommandID menuCommandID = new CommandID(GuidList.guidTrueGoToCmdSet, (int)PkgCmdIDList.cmdTrueGoTo);
                MenuCommand menuItem = new MenuCommand(MenuItemCallback, menuCommandID);
                mcs.AddCommand(menuItem);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            RemoveHandlers();
            for (int i = 0; i < _solutionElements.Count(); i++)
            {
                _solutionElements[i] = null;
            }
            _solutionElements = null;
            _codeEvents = null;
        }

        private static IEnumerable<T> ConvertToElementArray<T>(IEnumerable list)
        {
            foreach (T element in list)
                yield return element;
        }

        private void MenuItemCallback(object sender, EventArgs e)
        {
            if (_isInitialized && _DTE.Solution.IsOpen && _DTE.ActiveDocument != null && _DTE.ActiveDocument.Selection != null)
            {
                TextSelection selectedText = (TextSelection)_DTE.ActiveDocument.Selection;
                string target = GetWordFromSelection(selectedText);
                CodeElement targetElement = null;
                if (!String.IsNullOrWhiteSpace(target))
                {
                    lock (_solutionElementsLock)
                    {
                        targetElement = ReduceResultSet(_solutionElements, target);
                    }
                    if (targetElement != null)
                    {
                        ChangeActiveDocument(targetElement);
                        return;
                    }
                }
            }
            
            _DTE.ExecuteCommand("Edit.GoToDefinition");
        }

        private string GetWordFromSelection(TextSelection selection)
        {
            string target = selection.Text;

            selection.WordLeft(true);
            string leftWord = selection.Text;
            selection.WordRight(true);
            string rightWord = selection.Text;

            if (!(String.IsNullOrWhiteSpace(leftWord) || String.IsNullOrWhiteSpace(rightWord)))
            {
                string selectedWord = leftWord + rightWord;
                if (String.IsNullOrWhiteSpace(target) || Regex.Match(selectedWord, target, RegexOptions.IgnoreCase).Success)
                {
                    return selectedWord.Trim();
                }
            }

            return target;
        }

        private CodeElement ReduceResultSet(List<CodeElement> elements, string target)
        {
            List<CodeElement> codeElements = elements.Where(t => t.Name.Equals(target)).ToList();
            List<string> activeNamespaces = new List<string>();
            vsCMElement[] whiteList = new vsCMElement[] { vsCMElement.vsCMElementImportStmt, vsCMElement.vsCMElementUsingStmt, vsCMElement.vsCMElementIncludeStmt };

            if (codeElements != null && codeElements.Count > 0)
            {
                if (codeElements.Count == 1)
                    return codeElements[0];
                activeNamespaces = TrueGoToPackage.ConvertToElementArray<CodeElement>(_DTE.ActiveDocument.ProjectItem.FileCodeModel.CodeElements)
                    .Where(e => whiteList.Contains(e.Kind)).Select(e => ((CodeImport)e).Namespace).ToList();
                if (activeNamespaces.Count > 0)
                    return HandleFunctionResultSet(codeElements.Where(e => activeNamespaces.Any(a => e.FullName.Contains(a))));
                else
                    return HandleFunctionResultSet(codeElements);
            }
            return null;
        }


        private CodeElement HandleFunctionResultSet(IEnumerable<CodeElement> elements)
        {
            if (elements.All(e => e.Kind != vsCMElement.vsCMElementFunction))
                return elements.FirstOrDefault();
            else
                return elements.FirstOrDefault();
        }

        private void ChangeActiveDocument(CodeElement definingElement)
        {
            Window window = definingElement.ProjectItem.Open(EnvDTE.Constants.vsViewKindCode);
            window.Activate();
            TextSelection currentPoint = window.Document.Selection as TextSelection;
            currentPoint.MoveToDisplayColumn(definingElement.StartPoint.Line, definingElement.StartPoint.DisplayColumn);
        }

        private List<CodeElement> NavigateProjects(Projects projects)
        {
            List<CodeElement> types = new List<CodeElement>();

            foreach (Project p in projects)
            {
                types.AddRange(NavigateProjectItems(p.ProjectItems));
            }

            return types;
        }

        private CodeElement[] NavigateProjectItems(ProjectItems items)
        {
            List<CodeElement> codeElements = new List<CodeElement>();

            if (items != null)
            {
                foreach (ProjectItem item in items)
                {
                    if (item.SubProject != null)
                        codeElements.AddRange(NavigateProjectItems(item.SubProject.ProjectItems));
                    else
                        codeElements.AddRange(NavigateProjectItems(item.ProjectItems));
                    if (item.FileCodeModel != null)
                        codeElements.AddRange(NavigateCodeElements(item.FileCodeModel.CodeElements));
                }
            }

            return codeElements.ToArray();
        }

        private CodeElement[] NavigateCodeElements(CodeElements elements)
        {
            List<CodeElement> codeElements = new List<CodeElement>();
            CodeElements members = null;

            if (elements != null)
            {
                foreach (CodeElement element in elements)
                {
                    if (element.Kind != vsCMElement.vsCMElementDelegate)
                    {
                        members = GetMembers(element);
                        if (members != null)
                            codeElements.AddRange(NavigateCodeElements(members));
                    }

                    if (!_blackList.Contains(element.Kind))
                        codeElements.Add(element);
                }
            }

            return codeElements.ToArray();
        }

        private CodeElements GetMembers(CodeElement element)
        {
            if (element is CodeNamespace)
                return ((CodeNamespace)element).Members;
            else if (element is CodeType)
                return ((CodeType)element).Members;
            else if (element is CodeFunction)
                return ((CodeFunction)element).Parameters;
            else
                return null;
        }

        private void AddHandlers()
        {
            Events2 events2;
            events2 = (Events2)_DTE.Events;
            _codeEvents = events2.get_CodeModelEvents();

            _codeEvents.ElementAdded += new _dispCodeModelEvents_ElementAddedEventHandler(AddedEventHandler);
            _codeEvents.ElementChanged += new _dispCodeModelEvents_ElementChangedEventHandler(ChangedEventHandler);
            _codeEvents.ElementDeleted += new _dispCodeModelEvents_ElementDeletedEventHandler(DeletedEventHandler);

            IVsSolution ivsSolution = GetService(typeof(SVsSolution)) as IVsSolution;
            if (ivsSolution != null)
            {
                ivsSolution.AdviseSolutionEvents(this, out _solutionEventsCookie);
            }
        }

        private void AddedEventHandler(CodeElement element)
        {
            lock (_solutionElementsLock)
            {
                _solutionElements.Add(element);
            }
        }

        private void ChangedEventHandler(CodeElement element, vsCMChangeKind change)
        {
            lock (_solutionElementsLock)
            {
                _solutionElements.Add(element);
            }
        }

        private void DeletedEventHandler(object parent, CodeElement element)
        {
            lock (_solutionElementsLock)
            {
                _solutionElements.Remove(element);
            }
        }

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
        {
            return VSConstants.S_OK;
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
        {
            _loadedProjectCount++;
            if (_loadedProjectCount == _maxProjectCount)
            {
                lock (_solutionElementsLock)
                {
                    _solutionElements = NavigateProjects(_DTE.Solution.Projects);
                }
            }
            return VSConstants.S_OK;
        }

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            _maxProjectCount = _DTE.Solution.Projects.Count;
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeCloseSolution(object pUnkReserved)
        {
            return VSConstants.S_OK;
        }

        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
        {
            return VSConstants.S_OK;
        }

        private void RemoveHandlers()
        {
            _codeEvents.ElementAdded -= new _dispCodeModelEvents_ElementAddedEventHandler(AddedEventHandler);
            _codeEvents.ElementChanged -= new _dispCodeModelEvents_ElementChangedEventHandler(ChangedEventHandler);
            _codeEvents.ElementDeleted -= new _dispCodeModelEvents_ElementDeletedEventHandler(DeletedEventHandler);

            IVsSolution ivsSolution = GetService(typeof(SVsSolution)) as IVsSolution;
            if (_solutionEventsCookie != 0 && ivsSolution != null)
            {
                ivsSolution.UnadviseSolutionEvents(_solutionEventsCookie);
            }
        }
    }
}

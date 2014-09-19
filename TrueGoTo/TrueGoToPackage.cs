﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace Careerbuilder.TrueGoTo
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    ///
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the 
    /// IVsPackage interface and uses the registration attributes defined in the framework to 
    /// register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true)]
    // This attribute is used to register the information needed to show this package
    // in the Help/About dialog of Visual Studio.
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    // This attribute is needed to let the shell know that this package exposes some menus.
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(GuidList.guidTrueGoToPkgString)]
    public sealed class TrueGoToPackage : Package
    {
        private DTE2 _DTE;
        private CodeModelEvents _codeEvents;
        private readonly vsCMElement[] _blackList = new vsCMElement[] { vsCMElement.vsCMElementImportStmt, vsCMElement.vsCMElementUsingStmt, vsCMElement.vsCMElementAttribute };

        /// <summary>
        /// Default constructor of the package.
        /// Inside this method you can place any initialization code that does not require 
        /// any Visual Studio service because at this point the package object is created but 
        /// not sited yet inside Visual Studio environment. The place to do all the other 
        /// initialization is the Initialize method.
        /// </summary>
        public TrueGoToPackage()
        {
        }

        #region Overridden Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            base.Initialize();

            _DTE = (DTE2)GetService(typeof(DTE));

            // Add our command handlers for menu (commands must exist in the .vsct file)
            OleMenuCommandService mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (null != mcs)
            {
                // Create the command for the menu item.
                CommandID menuCommandID = new CommandID(GuidList.guidTrueGoToCmdSet, (int)PkgCmdIDList.cmdTrueGoTo);
                MenuCommand menuItem = new MenuCommand(MenuItemCallback, menuCommandID);
                mcs.AddCommand(menuItem);
            }
        }
        #endregion

        private static IEnumerable<T> ConvertToElementArray<T>(IEnumerable list)
        {
            foreach (T element in list)
                yield return element;
        }

        /// <summary>
        /// This function is the callback used to execute a command when the a menu item is clicked.
        /// See the Initialize method to see how the menu item is associated to this function using
        /// the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void MenuItemCallback(object sender, EventArgs e)
        {
            if (_DTE.Solution.IsOpen && _DTE.ActiveDocument != null && _DTE.ActiveDocument.Selection != null)
            {
                TextSelection selectedText = (TextSelection)_DTE.ActiveDocument.Selection;
                string target = GetWordFromSelection(selectedText);
                if (!String.IsNullOrWhiteSpace(target))
                {
                    List<CodeElement> codeElements = NavigateProjects(_DTE.Solution.Projects);
                    CodeElement targetElement = ReduceResultSet(codeElements, target);
                }
                return;
            }
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
                    return selectedWord;
                }
            }

            return target;
        }

        private CodeElement ReduceResultSet(List<CodeElement> elements, string target)
        {
            List<CodeElement> codeElements = elements.Where(t => t.Name.Equals(target)).ToList();
            List<string> activeNamespaces = new List<string>();
            vsCMElement[] whiteList = new vsCMElement[] { vsCMElement.vsCMElementImportStmt, vsCMElement.vsCMElementUsingStmt, vsCMElement.vsCMElementIncludeStmt };

            if (codeElements != null)
            {
                if (codeElements.Count == 1)
                    return codeElements[0];

                activeNamespaces = TrueGoToPackage.ConvertToElementArray<CodeElement>(_DTE.ActiveDocument.ProjectItem.FileCodeModel.CodeElements)
                    .Where(e => whiteList.Contains(e.Kind))
                    .Select(e => e.Name).ToList();

                return codeElements.Where(e => activeNamespaces.Any(a => e.FullName.Contains(a))).FirstOrDefault();

            }
            return null;
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

                    if (!_blackList.Contains(element.Kind) && element.IsCodeType)
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
            EnvDTE80.Events2 events2;
            events2 = (EnvDTE80.Events2)_DTE.Events;
            _codeEvents = events2.get_CodeModelEvents();

            _codeEvents.ElementAdded += new _dispCodeModelEvents_ElementAddedEventHandler(AddedEventHandler);
            _codeEvents.ElementChanged += new _dispCodeModelEvents_ElementChangedEventHandler(ChangedEventHandler);
            _codeEvents.ElementDeleted += new _dispCodeModelEvents_ElementDeletedEventHandler(DeletedEventHandler);
        }

        private void AddedEventHandler(CodeElement Element)
        {
            throw new NotImplementedException();
        }

        private void ChangedEventHandler(CodeElement Element, vsCMChangeKind Change)
        {
            throw new NotImplementedException();
        }

        private void DeletedEventHandler(object Parent, CodeElement Element)
        {
            throw new NotImplementedException();
        }

        private void RemoveHandlers()
        {
            _codeEvents.ElementAdded -= new _dispCodeModelEvents_ElementAddedEventHandler(AddedEventHandler);
            _codeEvents.ElementChanged -= new _dispCodeModelEvents_ElementChangedEventHandler(ChangedEventHandler);
            _codeEvents.ElementDeleted -= new _dispCodeModelEvents_ElementDeletedEventHandler(DeletedEventHandler);
        }
    }
}

// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Daniel Grunwald" email="daniel@danielgrunwald.de"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.TextEditor;
using ICSharpCode.TextEditor.Document;
using ReflectionLayer = ICSharpCode.SharpDevelop.Dom.ReflectionLayer;

namespace ICSharpCode.FormsDesigner
{
	public abstract class AbstractDesignerGenerator : IDesignerGenerator
	{
		/// <summary>The currently open part of the class being designed.</summary>
		IClass currentClassPart;
		/// <summary>The complete class being designed.</summary>
		IClass  completeClass;
		/// <summary>The class part containing the designer code.</summary>
		IClass  formClass;
		IMethod initializeComponents;
		
		FormsDesignerViewContent viewContent;
		CodeDomProvider provider;
		
		public CodeDomProvider CodeDomProvider {
			get {
				if (this.provider == null) {
					this.provider = this.CreateCodeProvider();
				}
				return this.provider;
			}
		}
		
		public FormsDesignerViewContent ViewContent {
			get {
				return viewContent;
			}
		}
		
		/// <summary>
		/// Gets the part of the designed class that is open in the source code editor which the designer view is attached to.
		/// </summary>
		protected IClass CurrentClassPart {
			get { return this.currentClassPart; }
			set { this.currentClassPart = value; }
		}
		
		public void Attach(FormsDesignerViewContent viewContent)
		{
			this.viewContent = viewContent;
		}
		
		public void Detach()
		{
			this.viewContent = null;
		}
		
		public OpenedFile DetermineDesignerCodeFile()
		{
			// get new initialize components
			ParseInformation info = ParserService.ParseFile(this.viewContent.PrimaryFileName, this.viewContent.PrimaryFileContent, false);
			ICompilationUnit cu = info.BestCompilationUnit;
			foreach (IClass c in cu.Classes) {
				if (FormsDesignerSecondaryDisplayBinding.BaseClassIsFormOrControl(c)) {
					this.currentClassPart = c;
					this.initializeComponents = FormsDesignerSecondaryDisplayBinding.GetInitializeComponents(c);
					if (this.initializeComponents != null) {
						string designerFile = this.initializeComponents.DeclaringType.CompilationUnit.FileName;
						if (designerFile != null) {
							return FileService.GetOrCreateOpenedFile(designerFile);
						}
					}
				}
			}
			
			throw new FormsDesignerLoadException("Could not find InitializeComponent method in any part of the open class.");
		}
		
		/// <summary>
		/// Removes the field declaration with the specified name from the source file.
		/// </summary>
		void RemoveField(string fieldName)
		{
			try {
				LoggingService.Info("Remove field declaration: "+fieldName);
				Reparse();
				IField field = GetField(formClass, fieldName);
				if (field != null) {
					int startOffset = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(0, field.Region.BeginLine - 1));
					int endOffset   = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(0, field.Region.EndLine));
					this.ViewContent.DesignerCodeFileDocument.Remove(startOffset, endOffset - startOffset);
				} else if ((field = GetField(completeClass, fieldName)) != null) {
					// TODO: Remove the field in the part where it is declared
					LoggingService.Warn("Removing field declaration in non-designer part currently not supported");
				}
			} catch (Exception ex) {
				MessageService.ShowError(ex);
			}
		}
		
		protected virtual string GenerateFieldDeclaration(CodeDOMGenerator domGenerator, CodeMemberField field)
		{
			StringWriter writer = new StringWriter();
			domGenerator.ConvertContentDefinition(field, writer);
			return writer.ToString().Trim();
		}
		
		/// <summary>
		/// Contains the tabs in front of the InitializeComponents declaration.
		/// Used to indent the fields and generated statements.
		/// </summary>
		protected string tabs;
		
		/// <summary>
		/// Adds the declaration for the specified field to the source file
		/// or replaces the already present declaration for a field with the same name.
		/// </summary>
		/// <param name="domGenerator">The CodeDOMGenerator used to generate the field declaration.</param>
		/// <param name="newField">The CodeDom field to be added or replaced.</param>
		void AddOrReplaceField(CodeDOMGenerator domGenerator, CodeMemberField newField)
		{
			try {
				Reparse();
				IField oldField = GetField(formClass, newField.Name);
				if (oldField != null) {
					int startOffset = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(0, oldField.Region.BeginLine - 1));
					int endOffset   = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(0, oldField.Region.EndLine));
					this.ViewContent.DesignerCodeFileDocument.Replace(startOffset, endOffset - startOffset, tabs + GenerateFieldDeclaration(domGenerator, newField) + Environment.NewLine);
				} else {
					if ((oldField = GetField(completeClass, newField.Name)) != null) {
						// TODO: Replace the field in the part where it is declared
						LoggingService.Warn("Field declaration replacement in non-designer part currently not supported");
					} else {
						int endOffset = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(0, initializeComponents.BodyRegion.EndLine));
						this.ViewContent.DesignerCodeFileDocument.Insert(endOffset, tabs + GenerateFieldDeclaration(domGenerator, newField) + Environment.NewLine);
					}
				}
			} catch (Exception ex) {
				MessageService.ShowError(ex);
			}
		}
		
		protected abstract System.CodeDom.Compiler.CodeDomProvider CreateCodeProvider();
		
		protected abstract DomRegion GetReplaceRegion(ICSharpCode.TextEditor.Document.IDocument document, IMethod method);
		
		protected virtual void FixGeneratedCode(IClass formClass, CodeMemberMethod code)
		{
		}
		
		public virtual void MergeFormChanges(CodeCompileUnit unit)
		{
			Reparse();
			
			// find InitializeComponent method and the class it is declared in
			CodeTypeDeclaration formClass = null;
			CodeMemberMethod initializeComponent = null;
			foreach (CodeNamespace n in unit.Namespaces) {
				foreach (CodeTypeDeclaration typeDecl in n.Types) {
					foreach (CodeTypeMember m in typeDecl.Members) {
						if (m is CodeMemberMethod && m.Name == "InitializeComponent") {
							formClass = typeDecl;
							initializeComponent = (CodeMemberMethod)m;
							break;
						}
					}
				}
			}
			
			if (formClass == null || initializeComponent == null) {
				throw new InvalidOperationException("InitializeComponent method not found in framework-generated CodeDom.");
			}
			if (this.formClass == null) {
				MessageService.ShowMessage("Cannot save form: InitializeComponent method does not exist anymore. You should not modify the Designer.cs file while editing a form.");
				return;
			}
			
			if (formClass.Name != this.formClass.Name) {
				LoggingService.Info("Renaming form to " + formClass.Name);
				Dictionary<string, IDocument> providedFileDocuments = new Dictionary<string, IDocument>();
				providedFileDocuments.Add(this.ViewContent.DesignerCodeFile.FileName, this.ViewContent.DesignerCodeFileDocument);
				if (!this.ViewContent.PrimaryFile.Equals(this.ViewContent.DesignerCodeFile)) {
					System.Diagnostics.Debug.Assert(!this.ViewContent.DesignerCodeFileDocument.Equals(this.ViewContent.PrimaryFileDocument));
					providedFileDocuments.Add(this.ViewContent.PrimaryFileName, this.ViewContent.PrimaryFileDocument);
				}
				ICSharpCode.SharpDevelop.Refactoring.FindReferencesAndRenameHelper.RenameClass(this.formClass, formClass.Name, providedFileDocuments);
				this.ViewContent.DesignerCodeFile.MakeDirty();
				this.ViewContent.PrimaryFile.MakeDirty();
				Reparse();
			}
			
			FixGeneratedCode(this.formClass, initializeComponent);
			
			// generate file and get initialize components string
			StringWriter writer = new StringWriter();
			CodeDOMGenerator domGenerator = new CodeDOMGenerator(this.CodeDomProvider, tabs + '\t');
			domGenerator.ConvertContentDefinition(initializeComponent, writer);
			
			string statements = writer.ToString();
			
			// initializeComponents.BodyRegion.BeginLine + 1
			DomRegion bodyRegion = GetReplaceRegion(this.ViewContent.DesignerCodeFileDocument, initializeComponents);
			if (bodyRegion.BeginColumn <= 0 || bodyRegion.EndColumn <= 0)
				throw new InvalidOperationException("Column must be > 0");
			int startOffset = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(bodyRegion.BeginColumn - 1, bodyRegion.BeginLine - 1));
			int endOffset   = this.ViewContent.DesignerCodeFileDocument.PositionToOffset(new TextLocation(bodyRegion.EndColumn - 1, bodyRegion.EndLine - 1));
			
			this.ViewContent.DesignerCodeFileDocument.Replace(startOffset, endOffset - startOffset, statements);
			
			// apply changes the designer made to field declarations
			// first loop looks for added and changed fields
			foreach (CodeTypeMember m in formClass.Members) {
				if (m is CodeMemberField) {
					CodeMemberField newField = (CodeMemberField)m;
					IField oldField = GetField(completeClass, newField.Name);
					if (oldField == null || FieldChanged(oldField, newField)) {
						AddOrReplaceField(domGenerator, newField);
					}
				}
			}
			
			// second loop looks for removed fields
			List<string> removedFields = new List<string>();
			foreach (IField field in completeClass.Fields) {
				bool found = false;
				foreach (CodeTypeMember m in formClass.Members) {
					if (m is CodeMemberField && m.Name == field.Name) {
						found = true;
						break;
					}
				}
				if (!found) {
					removedFields.Add(field.Name);
				}
			}
			// removing fields is done in two steps because
			// we must not modify the c.Fields collection while it is enumerated
			removedFields.ForEach(RemoveField);
			
			ParserService.EnqueueForParsing(this.ViewContent.DesignerCodeFile.FileName, this.ViewContent.DesignerCodeFileDocument.TextContent);
		}
		
		/// <summary>
		/// Compares the SharpDevelop.Dom field declaration oldField to
		/// the CodeDom field declaration newField.
		/// </summary>
		/// <returns>true, if the fields are different in type or modifiers, otherwise false.</returns>
		static bool FieldChanged(IField oldField, CodeMemberField newField)
		{
			// compare types
			if (oldField.ReturnType != null && oldField.ReturnType.GetUnderlyingClass() != null) { // ignore type changes to untyped VB fields
				if (oldField.ReturnType.GetUnderlyingClass().DotNetName != newField.Type.BaseType) {
					LoggingService.Debug("FieldChanged: "+oldField.Name+", "+oldField.ReturnType.FullyQualifiedName+" -> "+newField.Type.BaseType);
					return true;
				}
			}
			
			// compare modifiers
			ModifierEnum oldModifiers = oldField.Modifiers & ModifierEnum.VisibilityMask;
			MemberAttributes newModifiers = newField.Attributes & MemberAttributes.AccessMask;
			
			// SharpDevelop.Dom always adds Private modifier, even if not specified
			// CodeDom omits Private modifier if not present (although it is the default)
			if (oldModifiers == ModifierEnum.Private) {
				if (newModifiers != 0 && newModifiers != MemberAttributes.Private) {
					return true;
				}
			}
			
			ModifierEnum[] sdModifiers = new ModifierEnum[] {ModifierEnum.Protected, ModifierEnum.ProtectedAndInternal, ModifierEnum.Internal, ModifierEnum.Public};
			MemberAttributes[] cdModifiers = new MemberAttributes[] {MemberAttributes.Family, MemberAttributes.FamilyOrAssembly, MemberAttributes.Assembly, MemberAttributes.Public};
			for (int i = 0; i < sdModifiers.Length; i++) {
				if ((oldModifiers  == sdModifiers[i]) ^ (newModifiers  == cdModifiers[i])) {
					return true;
				}
			}
			
			return false;
		}
		
		protected void Reparse()
		{
			// get new initialize components
			ParseInformation info = ParserService.ParseFile(this.ViewContent.DesignerCodeFile.FileName, this.ViewContent.DesignerCodeFileContent, false);
			ICompilationUnit cu = info.BestCompilationUnit;
			foreach (IClass c in cu.Classes) {
				if (FormsDesignerSecondaryDisplayBinding.BaseClassIsFormOrControl(c)) {
					this.initializeComponents = FormsDesignerSecondaryDisplayBinding.GetInitializeComponents(c);
					if (this.initializeComponents != null) {
						using (StringReader r = new StringReader(this.ViewContent.DesignerCodeFileContent)) {
							int count = this.initializeComponents.Region.BeginLine;
							for (int i = 1; i < count; i++)
								r.ReadLine();
							string line = r.ReadLine();
							tabs = GetIndentation(line);
						}
						this.completeClass = c.GetCompoundClass();
						this.formClass = this.initializeComponents.DeclaringType;
						break;
					}
				}
			}
		}
		
		protected static string GetIndentation(string line)
		{
			return line.Substring(0, line.Length - line.TrimStart().Length);
		}
		
		protected abstract string CreateEventHandler(Type eventType, string eventMethodName, string body, string indentation);
		
		protected virtual int GetCursorLine(IDocument document, IMethod method)
		{
			return method.BodyRegion.BeginLine + 1;
		}
		
		protected virtual int GetCursorLineAfterEventHandlerCreation()
		{
			return 2;
		}
		
		/// <summary>
		/// If found return true and int as position
		/// </summary>
		/// <param name="component"></param>
		/// <param name="edesc"></param>
		/// <returns></returns>
		public virtual bool InsertComponentEvent(IComponent component, EventDescriptor edesc, string eventMethodName, string body, out string file, out int position)
		{
			Reparse();
			
			foreach (IMethod method in completeClass.Methods) {
				if (method.Name == eventMethodName) {
					file = method.DeclaringType.CompilationUnit.FileName;
					if (FileUtility.IsEqualFileName(file, this.ViewContent.PrimaryFileName)) {
						position = GetCursorLine(this.ViewContent.PrimaryFileDocument, method);
					} else if (FileUtility.IsEqualFileName(file, this.ViewContent.DesignerCodeFile.FileName)) {
						position = GetCursorLine(this.ViewContent.DesignerCodeFileDocument, method);
					} else {
						try {
							position = GetCursorLine(FindReferencesAndRenameHelper.GetDocumentInformation(file).CreateDocument(), method);
						} catch (FileNotFoundException) {
							position = 0;
							return false;
						}
					}
					return true;
				}
			}
			
			viewContent.MergeFormChanges();
			Reparse();
			
			file = currentClassPart.CompilationUnit.FileName;
			int line = GetEventHandlerInsertionLine(currentClassPart);
			
			int offset = this.viewContent.PrimaryFileDocument.GetLineSegment(line - 1).Offset;
			
			this.viewContent.PrimaryFileDocument.Insert(offset, CreateEventHandler(edesc.EventType, eventMethodName, body, tabs));
			position = line + GetCursorLineAfterEventHandlerCreation();
			this.viewContent.PrimaryFile.MakeDirty();
			
			return true;
		}
		
		/// <summary>
		/// Gets a method implementing the signature specified by the event descriptor
		/// </summary>
		protected static IMethod ConvertEventInvokeMethodToDom(IClass declaringType, Type eventType, string methodName)
		{
			MethodInfo mInfo = eventType.GetMethod("Invoke");
			DefaultMethod m = new DefaultMethod(declaringType, methodName);
			m.ReturnType = ReflectionLayer.ReflectionReturnType.Create(m, mInfo.ReturnType, false);
			foreach (ParameterInfo pInfo in mInfo.GetParameters()) {
				m.Parameters.Add(new ReflectionLayer.ReflectionParameter(pInfo, m));
			}
			return m;
		}
		
		/// <summary>
		/// Gets a method implementing the signature specified by the event descriptor
		/// </summary>
		protected static ICSharpCode.NRefactory.Ast.MethodDeclaration
			ConvertEventInvokeMethodToNRefactory(IClass context, Type eventType, string methodName)
		{
			if (context == null)
				throw new ArgumentNullException("context");
			
			return ICSharpCode.SharpDevelop.Dom.Refactoring.CodeGenerator.ConvertMember(
				ConvertEventInvokeMethodToDom(context, eventType, methodName),
				new ClassFinder(context, context.BodyRegion.BeginLine + 1, 1)
			) as ICSharpCode.NRefactory.Ast.MethodDeclaration;
		}
		
		protected virtual int GetEventHandlerInsertionLine(IClass c)
		{
			return c.Region.EndLine;
		}
		
		public virtual ICollection GetCompatibleMethods(EventDescriptor edesc)
		{
			Reparse();
			ArrayList compatibleMethods = new ArrayList();
			MethodInfo methodInfo = edesc.EventType.GetMethod("Invoke");
			foreach (IMethod method in completeClass.Methods) {
				if (method.Parameters.Count == methodInfo.GetParameters().Length) {
					bool found = true;
					for (int i = 0; i < methodInfo.GetParameters().Length; ++i) {
						ParameterInfo pInfo = methodInfo.GetParameters()[i];
						IParameter p = method.Parameters[i];
						if (p.ReturnType.FullyQualifiedName != pInfo.ParameterType.ToString()) {
							found = false;
							break;
						}
					}
					if (found) {
						compatibleMethods.Add(method.Name);
					}
				}
			}
			
			return compatibleMethods;
		}
		
		protected IField GetField(IClass c, string name)
		{
			foreach (IField field in c.Fields) {
				if (field.Name == name) {
					return field;
				}
			}
			return null;
		}
	}
}

﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClangSharp;

namespace BulletSharpGen
{
    class ReaderContext
    {
        public TranslationUnit TranslationUnit { get; set; }
        public string HeaderFilename { get; set; }
        public HeaderDefinition Header { get; set; }
        public string Namespace { get; set; } = "";
        public ClassDefinition Class { get; set; }
        public MethodDefinition Method { get; set; }
        public ParameterDefinition Parameter { get; set; }

        public AccessSpecifier MemberAccess { get; set; }
    }

    class CppReader
    {
        List<string> headerQueue = new List<string>();
        List<string> clangOptions = new List<string>();
        HashSet<string> excludedMethods = new HashSet<string>();

        ReaderContext _context = new ReaderContext();
        WrapperProject project;

        public CppReader(WrapperProject project)
        {
            this.project = project;

            foreach (string sourceRelDir in project.SourceRootFoldersFull)
            {
                string sourceFullDir = Path.GetFullPath(sourceRelDir).Replace('\\', '/');

                // Enumerate all header files in the source tree
                var headerFiles = Directory.EnumerateFiles(sourceFullDir, "*.h", SearchOption.AllDirectories);
                foreach (string headerFullDir in headerFiles)
                {
                    string headerFullDirCanonical = headerFullDir.Replace('\\', '/');
                    //string headerRelDir = headerFullDirCanonical.Substring(sourceFullDir.Length);

                    HeaderDefinition header;
                    if (project.HeaderDefinitions.TryGetValue(headerFullDirCanonical, out header))
                    {
                        if (header.IsExcluded)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        Console.WriteLine("New file {0}", headerFullDirCanonical);
                    }

                    headerQueue.Add(headerFullDirCanonical);
                }

                // Include directory
                clangOptions.Add("-I");
                clangOptions.Add(sourceFullDir);
            }

            // WorldImporter include directory
            //clangOptions.Add("-I");
            //clangOptions.Add(src + "../Extras/Serialize/BulletWorldImporter");

            // Specify C++ headers, not C ones
            clangOptions.Add("-x");
            clangOptions.Add("c++-header");

            //clangOptions.Add("-DUSE_DOUBLE_PRECISION");

            // Exclude irrelevant methods
            excludedMethods.Add("operator new");
            excludedMethods.Add("operator delete");
            excludedMethods.Add("operator new[]");
            excludedMethods.Add("operator delete[]");
            excludedMethods.Add("operator+=");
            excludedMethods.Add("operator-=");
            excludedMethods.Add("operator*=");
            excludedMethods.Add("operator/=");
            excludedMethods.Add("operator==");
            excludedMethods.Add("operator!=");
            excludedMethods.Add("operator()");
            excludedMethods.Add("operator=");

            Console.Write("Reading headers");

            ReadHeaders();

            foreach (var @class in project.ClassDefinitions.Values.Where(c => !c.IsParsed))
            {
                Console.WriteLine("Class removed: {0}", @class.FullyQualifiedName);
            }
        }

        Cursor.ChildVisitResult HeaderVisitor(Cursor cursor, Cursor parent)
        {
            string filename = cursor.Extent.Start.File.Name.Replace('\\', '/');

            // Do not visit any included header
            if (!filename.Equals(_context.HeaderFilename)) return Cursor.ChildVisitResult.Continue;

            headerQueue.Remove(filename);

            // Have we visited this header already?
            HeaderDefinition header;
            if (!project.HeaderDefinitions.TryGetValue(filename, out header))
            {
                // No, define a new one
                header = new HeaderDefinition(filename);
                project.HeaderDefinitions[filename] = header;
            }
            _context.Header = header;

            if (cursor.IsDefinition)
            {
                switch (cursor.Kind)
                {
                    case CursorKind.ClassDecl:
                    case CursorKind.ClassTemplate:
                    case CursorKind.EnumDecl:
                    case CursorKind.StructDecl:
                        ParseClassCursor(cursor);
                        break;
                    case CursorKind.TypedefDecl:
                        ParseTypedefCursor(cursor);
                        break;
                    case CursorKind.Namespace:
                        _context.Namespace = cursor.Spelling;
                        cursor.VisitChildren(HeaderVisitor);
                        _context.Namespace = "";
                        break;
                }
            }

            return Cursor.ChildVisitResult.Continue;
        }

        void ParseTemplateBaseCursor(Cursor cursor)
        {
            var tokens = _context.TranslationUnit.Tokenize(cursor.Extent)
                .TakeWhile(t => !t.Spelling.Equals("{"))
                .SkipWhile(t => !t.Spelling.Equals(":"));
            if (!tokens.Any()) return;

            var baseTokens = tokens.ToList();
            int templSpecStart = -1, templSpecEnd = -1;
            for (int i = 0; i < baseTokens.Count; i++)
            {
                var token = baseTokens[i];
                if (token.Spelling == "<")
                {
                    templSpecStart = i;
                }
                else if (token.Spelling == ">")
                {
                    templSpecEnd = i;
                }
            }

            if (templSpecStart != -1 && templSpecEnd != -1)
            {
                string template = baseTokens[templSpecStart - 1].Spelling;
                string templateSpec = string.Join(" ",
                    baseTokens.GetRange(templSpecStart + 1, templSpecEnd - templSpecStart - 1)
                    .Select(t => t.Spelling));
                templateSpec = TypeRefDefinition.GetBasicName(templateSpec);

                string templateName = $"{template}<{templateSpec}>";
                ClassDefinition classTemplate;
                if (!project.ClassDefinitions.TryGetValue(templateName, out classTemplate))
                {
                    var classTemplateNew = new ClassTemplateDefinition(template, _context.Header);
                    classTemplateNew.TemplateParameters.Add(templateSpec);

                    var baseTemplate = project.ClassDefinitions.FirstOrDefault(c => c.Value.Name.Equals(template));
                    classTemplateNew.BaseClass = baseTemplate.Value;

                    project.ClassDefinitions[templateName] = classTemplateNew;
                    _context.Header.Classes.Add(classTemplateNew);
                    classTemplate = classTemplateNew;
                }

                _context.Class.BaseClass = classTemplate;
            }
        }

        void ParseClassCursor(Cursor cursor)
        {
            string className = cursor.Spelling;
            string fullyQualifiedName = TypeRefDefinition.GetFullyQualifiedName(cursor);

            ClassDefinition @class;
            if (project.ClassDefinitions.TryGetValue(fullyQualifiedName, out @class))
            {
                if (@class.IsParsed) return;

                @class.Parent = _context.Class;
            }
            else
            {
                switch(cursor.Kind)
                {
                    case CursorKind.ClassTemplate:
                        @class = new ClassTemplateDefinition(className, _context.Header, _context.Class);
                        break;
                    case CursorKind.EnumDecl:
                        @class = new EnumDefinition(className, _context.Header, _context.Class);
                        break;
                    default:
                        @class = new ClassDefinition(className, _context.Header, _context.Class);
                        break;
                }

                @class.NamespaceName = _context.Namespace;

                if (@class.FullyQualifiedName != fullyQualifiedName)
                {
                    Console.WriteLine("Parsing error at " + fullyQualifiedName);
                }
                project.ClassDefinitions[fullyQualifiedName] = @class;
            }

            _context.Class = @class;
            _context.Class.IsParsed = true;

            // Unnamed struct escapes to the surrounding scope
            if (!(string.IsNullOrEmpty(className) && cursor.Kind == CursorKind.StructDecl))
            {
                if (_context.Class.Parent != null)
                {
                    _context.Class.Parent.NestedClasses.Add(_context.Class);
                }
                else
                {
                    _context.Header.Classes.Add(_context.Class);
                }
            }

            AccessSpecifier parentMemberAccess = _context.MemberAccess;

            // Default class/struct access specifier
            switch (cursor.Kind)
            {
                case CursorKind.ClassDecl:
                    _context.MemberAccess = AccessSpecifier.Private;
                    break;
                case CursorKind.StructDecl:
                    _context.Class.IsStruct = true;
                    _context.MemberAccess = AccessSpecifier.Public;
                    break;
                case CursorKind.ClassTemplate:
                    if (cursor.TemplateCursorKind != CursorKind.ClassDecl)
                    {
                        _context.MemberAccess = AccessSpecifier.Private;
                    }
                    else
                    {
                        _context.MemberAccess = AccessSpecifier.Public;
                    }
                    break;
                case CursorKind.TypedefDecl:
                    var underlying = cursor.TypedefDeclUnderlyingType.Canonical;
                    if (underlying.TypeKind == ClangSharp.Type.Kind.Pointer &&
                        underlying.Pointee.TypeKind == ClangSharp.Type.Kind.FunctionProto)
                    {
                        _context.Class.IsFunctionProto = true;
                    }
                    break;
            }

            if (cursor.Kind == CursorKind.EnumDecl)
            {
                var @enum = _context.Class as EnumDefinition;

                foreach (var constantDecl in cursor.Children
                    .Where(c => c.Kind == CursorKind.EnumConstantDecl))
                {
                    string valueSpelling = "";
                    var value = constantDecl.Children.FirstOrDefault();
                    if (value != null)
                    {
                        var valueTokens = _context.TranslationUnit.Tokenize(value.Extent)
                            .Where(t => t.Kind != TokenKind.Comment &&
                                !t.Spelling.Equals(",") &&
                                !t.Spelling.Equals("}"));
                        valueSpelling = string.Join("", valueTokens.Select(t => t.Spelling));
                    }
                    @enum.EnumConstants.Add(new EnumConstant(constantDecl.Spelling, valueSpelling));
                }
            }
            else
            {
                cursor.VisitChildren(ClassVisitor);

                if (_context.Class.BaseClass == null)
                {
                    // Clang doesn't give the base class if it's a template,
                    // tokenize the class definition and extract the base template if it exists
                    ParseTemplateBaseCursor(cursor);
                }
            }

            // Restore parent state
            _context.Class = _context.Class.Parent;
            _context.MemberAccess = parentMemberAccess;
        }

        Cursor.ChildVisitResult ClassVisitor(Cursor cursor, Cursor parent)
        {
            switch (cursor.Kind)
            {
                case CursorKind.CxxAccessSpecifier:
                    _context.MemberAccess = cursor.AccessSpecifier;
                    return Cursor.ChildVisitResult.Continue;
                case CursorKind.CxxBaseSpecifier:
                    string baseName = TypeRefDefinition.GetFullyQualifiedName(cursor.Type.Declaration);
                    ClassDefinition baseClass;
                    if (!project.ClassDefinitions.TryGetValue(baseName, out baseClass))
                    {
                        Console.WriteLine("Base {0} for {1} not found! Missing header?", baseName, _context.Class.Name);
                        return Cursor.ChildVisitResult.Continue;
                    }
                    _context.Class.BaseClass = baseClass;
                    return Cursor.ChildVisitResult.Continue;
                case CursorKind.TemplateTypeParameter:
                    var classTemplate = _context.Class as ClassTemplateDefinition;
                    if (!classTemplate.TemplateParameters.Contains(cursor.Spelling))
                    {
                        classTemplate.TemplateParameters.Add(cursor.Spelling);
                    }
                    return Cursor.ChildVisitResult.Continue;
            }

            // We usually only care about public members
            if (_context.MemberAccess != AccessSpecifier.Public)
            {
                if (cursor.IsVirtualCxxMethod && !cursor.IsPureVirtualCxxMethod)
                {
                    // private/protected virtual method that may override public abstract methods,
                    // necessary for checking whether a class is abstract or not.
                }
                else if (cursor.Kind == CursorKind.Constructor)
                {
                    // class has a private/protected constructor,
                    // no need to create a default constructor
                }
                else
                {
                    return Cursor.ChildVisitResult.Continue;
                }
            }

            switch (cursor.Kind)
            {
                case CursorKind.ClassDecl:
                case CursorKind.StructDecl:
                case CursorKind.ClassTemplate:
                case CursorKind.EnumDecl:
                    if (cursor.IsDefinition)
                    {
                        ParseClassCursor(cursor);
                    }
                    break;


                case CursorKind.CxxMethod:
                case CursorKind.Constructor:
                    ParseMethodCursor(cursor);
                    break;
                case CursorKind.ConversionFunction:
                case CursorKind.FunctionTemplate:
                case CursorKind.Destructor:
                    break;

                case CursorKind.FieldDecl:
                    var field = new FieldDefinition(cursor.Spelling,
                        new TypeRefDefinition(cursor.Type, cursor), _context.Class);
                    break;

                case CursorKind.TypedefDecl:
                    ParseTypedefCursor(cursor);
                    break;

                case CursorKind.UnionDecl:
                    return Cursor.ChildVisitResult.Recurse;

                default:
                    //Console.WriteLine(cursor.Spelling);
                    break;
            }

            return Cursor.ChildVisitResult.Continue;
        }

        private void ParseTypedefCursor(Cursor cursor)
        {
            var underlying = cursor.TypedefDeclUnderlyingType.Canonical;

            // Does the typedef include a type declaration?
            if (cursor.Children.Any(c => c.Equals(underlying.Declaration)))
            {
                ParseClassCursor(underlying.Declaration);
                return;
            }

            // Function pointer
            if (underlying.TypeKind == ClangSharp.Type.Kind.Pointer &&
                underlying.Pointee.TypeKind == ClangSharp.Type.Kind.FunctionProto)
            {
                ParseClassCursor(cursor);
            }
        }

        private void ParseMethodCursor(Cursor cursor)
        {
            string methodName = cursor.Spelling;
            if (excludedMethods.Contains(methodName)) return;

            var existingMethodsMatch = _context.Class.Methods.Where(
                m => !m.IsParsed && methodName.Equals(m.Name) &&
                     m.Parameters.Length == cursor.NumArguments);
            int existingCount = existingMethodsMatch.Count();
            if (existingCount == 1)
            {
                // TODO: check method parameter types if given
                _context.Method = existingMethodsMatch.First();
            }
            else if (existingCount >= 2)
            {
                Console.WriteLine("Ambiguous method in project: " + methodName);
            }

            if (_context.Method == null)
            {
                _context.Method = new MethodDefinition(methodName, _context.Class, cursor.NumArguments);
            }

            _context.Method.ReturnType = new TypeRefDefinition(cursor.ResultType, cursor);
            _context.Method.IsStatic = cursor.IsStaticCxxMethod;
            _context.Method.IsVirtual = cursor.IsVirtualCxxMethod;
            _context.Method.IsAbstract = cursor.IsPureVirtualCxxMethod;
            _context.Method.Access = cursor.AccessSpecifier;
            _context.Method.IsConstructor = cursor.Kind == CursorKind.Constructor;

            // Parse arguments
            for (uint i = 0; i < cursor.NumArguments; i++)
            {
                Cursor arg = cursor.GetArgument(i);

                if (_context.Method.Parameters[i] == null)
                {
                    _context.Parameter = new ParameterDefinition(arg.Spelling, new TypeRefDefinition(arg.Type, arg));
                    _context.Method.Parameters[i] = _context.Parameter;
                }
                else
                {
                    _context.Parameter = _context.Method.Parameters[i];
                    _context.Parameter.Type = new TypeRefDefinition(arg.Type);
                }

                // Check for a default value (optional parameter)
                var argTokens = _context.TranslationUnit.Tokenize(arg.Extent);
                if (argTokens.Any(a => a.Spelling.Equals("=")))
                {
                    _context.Parameter.IsOptional = true;
                }

                // Get marshalling direction
                if (_context.Parameter.MarshalDirection == MarshalDirection.Default)
                {
                    _context.Parameter.MarshalDirection = _context.Parameter.Type.GetDefaultMarshalDirection();
                }

                _context.Parameter = null;
            }

            // Discard any private/protected virtual method unless it
            // implements a public abstract method
            if (_context.MemberAccess != AccessSpecifier.Public && !_context.Method.IsConstructor)
            {
                if (_context.Method.Parent.BaseClass == null ||
                    !_context.Method.Parent.BaseClass.AbstractMethods.Contains(_context.Method))
                {
                    _context.Method.Parent.Methods.Remove(_context.Method);
                }
            }

            _context.Method.IsParsed = true;
            _context.Method = null;
        }

        private void ReadHeaders()
        {
            using (var index = new Index())
            {
                while (headerQueue.Any())
                {
                    _context.HeaderFilename = headerQueue.First();
                    Console.Write('.');

                    var unsavedFiles = new UnsavedFile[] {};
                    using (_context.TranslationUnit = index.CreateTranslationUnit(_context.HeaderFilename,
                        clangOptions.ToArray(), unsavedFiles, TranslationUnitFlags.SkipFunctionBodies))
                    {
                        var cur = _context.TranslationUnit.Cursor;
                        cur.VisitChildren(HeaderVisitor);
                    }
                    _context.TranslationUnit = null;
                    headerQueue.Remove(_context.HeaderFilename);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Read complete - headers: {0}, classes: {1}",
                project.HeaderDefinitions.Count, project.ClassDefinitions.Count);
        }
    }
}

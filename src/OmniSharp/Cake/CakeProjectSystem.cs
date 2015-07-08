#if DNX451
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Framework.Logging;
using OmniSharp.Services;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Core.IO.NuGet;
using Cake.Core.Scripting;
using CakeLogLevel = Cake.Core.Diagnostics.LogLevel;
using Path = System.IO.Path;

namespace OmniSharp.Cake
{
    public class CakeProjectSystem : IProjectSystem
    {
        private static readonly string BaseAssemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
        private readonly OmnisharpWorkspace _workspace;
        private readonly IOmnisharpEnvironment _env;
        private readonly CakeContext _cakeContext;
        private readonly ILogger _logger;
        private readonly ICakeEnvironment _enviroment = new CakeEnvironment();
        private readonly IFileSystem _fileSystem = new FileSystem();
        private readonly ICakeLog _cakeLogger;
        private readonly INuGetToolResolver _nugetToolResolver;

        public CakeProjectSystem(OmnisharpWorkspace workspace, IOmnisharpEnvironment env, ILoggerFactory loggerFactory, CakeContext cakeContext)
        {
            _workspace = workspace;
            _env = env;
            _cakeContext = cakeContext;
            _logger = loggerFactory.CreateLogger<CakeProjectSystem>();
            _cakeLogger = new OmniSharpCakeLog(_logger);
            var globber = new Globber(_fileSystem, _enviroment);
            _nugetToolResolver = new NuGetToolResolver(_fileSystem, _enviroment, globber);
        }

        public void Initalize()
        {
            _logger.LogInformation($"Detecting Cake files in '{_env.Path}'.");

            var allCakeFiles = Directory.GetFiles(_env.Path, "*.cake", SearchOption.TopDirectoryOnly);

            if (allCakeFiles.Length == 0)
            {
                _logger.LogInformation("Could not find any Cake files");
                return;
            }

            _cakeContext.Path = _env.Path;
            _logger.LogInformation($"Found {allCakeFiles.Length} Cake files.");

            var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp6, DocumentationMode.Parse, SourceCodeKind.Script);

            var runnerType = typeof(ScriptRunner);
            var usingsMethod = runnerType.GetMethod("GetDefaultNamespaces", BindingFlags.Static);
            Func<IEnumerable<string>> getUsings = () =>
            {
                return (IEnumerable<string>)usingsMethod.Invoke(null, new object[] { });
            };

            var assembliesMethod = runnerType.GetMethod("GetDefaultAssemblies", BindingFlags.Static);
            Func<IEnumerable<Assembly>> getAssemblies = () =>
            {
                return (IEnumerable<Assembly>)assembliesMethod.Invoke(null, new object[] { _fileSystem });
            };

            var cakeProcessor = CreateScriptProcessor();
            var cakeProcessorContext = new ScriptProcessorContext();

            foreach (var cakePath in allCakeFiles)
            {
                try
                {
                    _cakeContext.CakeFiles.Add(cakePath);
                    cakeProcessor.Process((FilePath)cakePath, cakeProcessorContext);

                    var references = new List<MetadataReference>();
                    var usings = new List<string>(getUsings().ToList());

                    //default references
                    ImportReferences(references, getAssemblies().Select((assembly) => assembly.Location));

                    //file usings
                    usings.AddRange(cakeProcessorContext.Namespaces);

                    //#r references
                    ImportReferences(references, cakeProcessorContext.References);

                    _cakeContext.References.UnionWith(references.Select(x => x.Display));
                    _cakeContext.Usings.UnionWith(usings);

                    var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, usings: usings.Distinct());

                    var fileName = Path.GetFileName(cakePath);

                    var projectId = ProjectId.CreateNewId(Guid.NewGuid().ToString());
                    var project = ProjectInfo.Create(projectId, VersionStamp.Create(), fileName, $"{fileName}.dll", LanguageNames.CSharp, null, null,
                                                            compilationOptions, parseOptions, null, null, references, null, null, true, typeof(IScriptHost));

                    _workspace.AddProject(project);
                    AddFile(cakePath, projectId);

                    foreach (var filePath in cakeProcessorContext.ProcessedScripts.Distinct().Except(new[] { cakePath }))
                    {
                        _cakeContext.CakeFiles.Add(filePath);
                        var loadedFileName = Path.GetFileName(filePath);

                        var loadedFileProjectId = ProjectId.CreateNewId(Guid.NewGuid().ToString());
                        var loadedFileSubmissionProject = ProjectInfo.Create(loadedFileProjectId, VersionStamp.Create(),
                            $"{loadedFileName}-LoadedFrom-{fileName}", $"{loadedFileName}-LoadedFrom-{fileName}.dll", LanguageNames.CSharp, null, null,
                                            compilationOptions, parseOptions, null, null, references, null, null, true, typeof(IScriptHost));

                        _workspace.AddProject(loadedFileSubmissionProject);
                        AddFile(filePath, loadedFileProjectId);
                        _workspace.AddProjectReference(projectId, new ProjectReference(loadedFileProjectId));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"{cakePath} will be ignored due to the following error:", ex);
                }
            }
        }

        private void ImportReferences(List<MetadataReference> listOfReferences, IEnumerable<string> referencesToImport)
        {
            foreach (FilePath importedReference in referencesToImport.Where(x => !x.ToLowerInvariant().Contains("cake.core")))
            {
                if (!importedReference.IsRelative)
                {
                    if (_fileSystem.Exist(importedReference))
                        listOfReferences.Add(MetadataReference.CreateFromFile(importedReference.FullPath));
                }
                else
                {
                    listOfReferences.Add(MetadataReference.CreateFromFile(importedReference.MakeAbsolute((DirectoryPath)BaseAssemblyPath).FullPath));
                }
            }
        }

        private void AddFile(string filePath, ProjectId projectId)
        {
            using (var stream = File.OpenRead(filePath))
            using (var reader = new StreamReader(stream))
            {
                var fileName = Path.GetFileName(filePath);
                var cakeFile = reader.ReadToEnd();

                var documentId = DocumentId.CreateNewId(projectId, fileName);
                var documentInfo = DocumentInfo.Create(documentId, fileName, null, SourceCodeKind.Script, null, filePath)
                    .WithSourceCodeKind(SourceCodeKind.Script)
                    .WithTextLoader(TextLoader.From(TextAndVersion.Create(SourceText.From(cakeFile), VersionStamp.Create())));
                _workspace.AddDocument(documentInfo);
            }
        }

        private IScriptProcessor CreateScriptProcessor() {
            return new ScriptProcessor(_fileSystem, _enviroment, _cakeLogger, _nugetToolResolver);
        }
    }

    internal sealed class OmniSharpCakeLog : ICakeLog {
        private readonly ILogger _logger;

        public OmniSharpCakeLog(ILogger logger) {
            _logger = logger;
        }

        public void Write(Verbosity verbosity, CakeLogLevel level, string format, params object[] args) {
            switch(level) {
                case CakeLogLevel.Debug:
                    _logger.LogDebug(format, args);
                    break;
                case CakeLogLevel.Verbose:
                    _logger.LogVerbose(format, args);
                    break;
                case CakeLogLevel.Information:
                    _logger.LogInformation(format, args);
                    break;
                case CakeLogLevel.Warning:
                    _logger.LogWarning(format, args);
                    break;
                case CakeLogLevel.Error:
                    _logger.LogError(format, args);
                    break;
            }
        }

        public Verbosity Verbosity { get; set; }
    }
}
#endif

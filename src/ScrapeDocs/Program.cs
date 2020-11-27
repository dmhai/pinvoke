﻿// Copyright © .NET Foundation and Contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace ScrapeDocs
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using YamlDotNet.RepresentationModel;

    internal class Program
    {
        private static readonly Regex FileNamePattern = new Regex(@"^\w\w-\w+-([\w\-]+)$", RegexOptions.Compiled);
        private static readonly Regex ParameterHeaderPattern = new Regex(@"^### -param (\w+)", RegexOptions.Compiled);
        private static readonly Regex FieldHeaderPattern = new Regex(@"^### -field (\w+)", RegexOptions.Compiled);
        private static readonly Regex ReturnHeaderPattern = new Regex(@"^## -returns", RegexOptions.Compiled);
        private readonly string contentBasePath;
        private readonly string outputPath;

        private Program(string contentBasePath, string outputPath)
        {
            this.contentBasePath = contentBasePath;
            this.outputPath = outputPath;
        }

        private static int Main(string[] args)
        {
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                Console.WriteLine("Canceling...");
                cts.Cancel();
                e.Cancel = true;
            };

            if (args.Length != 2)
            {
                Console.Error.WriteLine("USAGE: {0} <path-to-docs> <path-to-output-yml>");
                return 1;
            }

            string contentBasePath = args[0];
            string outputPath = args[1];

            try
            {
                new Program(contentBasePath, outputPath).Worker(cts.Token);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cts.Token)
            {
                return 2;
            }

            return 0;
        }

        private static void Expect(string? expected, string? actual)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException($"Expected: \"{expected}\" but read: \"{actual}\".");
            }
        }

        private void Worker(CancellationToken cancellationToken)
        {
            Console.WriteLine("Enumerating documents to be parsed...");
            string[] paths = Directory.GetFiles(this.contentBasePath, "??-*-*.md", SearchOption.AllDirectories)
                ////.Where(p => Path.GetFileNameWithoutExtension(p).Contains("lastinputinfo")).ToArray()
                ;

            Console.WriteLine("Parsing documents...");
            var timer = Stopwatch.StartNew();
            var parsedNodes = from path in paths.AsParallel()
                              let result = this.ParseDocFile(path)
                              where result is { }
                              select (Path: path, result.Value.ApiName, result.Value.YamlNode);
            var results = new ConcurrentDictionary<YamlNode, YamlNode>();
            if (Debugger.IsAttached)
            {
                parsedNodes = parsedNodes.WithDegreeOfParallelism(1); // improve debuggability
            }

            parsedNodes
                .WithCancellation(cancellationToken)
                .ForAll(result => results.TryAdd(new YamlScalarNode(result.ApiName), result.YamlNode));
            Console.WriteLine("Parsed {2} documents in {0} ({1} per document)", timer.Elapsed, timer.Elapsed / paths.Length, paths.Length);

            Console.WriteLine("Writing results to \"{0}\"", this.outputPath);
            var yamlDocument = new YamlDocument(new YamlMappingNode(results));
            var yamlStream = new YamlStream(yamlDocument);
            Directory.CreateDirectory(Path.GetDirectoryName(this.outputPath));
            using var yamlWriter = File.CreateText(this.outputPath);
            yamlWriter.WriteLine($"# This file was generated by the {Assembly.GetExecutingAssembly().GetName().Name} tool in this repo.");
            yamlStream.Save(yamlWriter);
        }

        private (string ApiName, YamlNode YamlNode)? ParseDocFile(string filePath)
        {
            string presumedMethodName = FileNamePattern.Match(Path.GetFileNameWithoutExtension(filePath)).Groups[1].Value;
            Uri helpLink = new Uri("https://docs.microsoft.com/en-us/windows/win32/api/" + filePath.Substring(this.contentBasePath.Length, filePath.Length - 3 - this.contentBasePath.Length).Replace('\\', '/'));

            var yaml = new YamlStream();
            using StreamReader mdFileReader = File.OpenText(filePath);
            using var markdownToYamlReader = new YamlSectionReader(mdFileReader);
            var yamlBuilder = new StringBuilder();
            string? line;
            while ((line = markdownToYamlReader.ReadLine()) is object)
            {
                yamlBuilder.AppendLine(line);
            }

            try
            {
                yaml.Load(new StringReader(yamlBuilder.ToString()));
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                Debug.WriteLine("YAML parsing error in \"{0}\": {1}", filePath, ex.Message);
                return null;
            }

            var methodNames = (YamlSequenceNode)yaml.Documents[0].RootNode["api_name"];
            string? properName = methodNames.Children.Cast<YamlScalarNode>().FirstOrDefault(c => string.Equals(c.Value?.Replace('.', '-'), presumedMethodName, StringComparison.OrdinalIgnoreCase))?.Value;
            if (properName is null)
            {
                Debug.WriteLine("WARNING: Could not find proper API name in: {0}", filePath);
                return null;
            }

            var methodNode = new YamlMappingNode();
            methodNode.Add("HelpLink", helpLink.AbsoluteUri);

            var description = ((YamlMappingNode)yaml.Documents[0].RootNode).Children.FirstOrDefault(n => n.Key is YamlScalarNode { Value: "description" }).Value as YamlScalarNode;
            if (description is object)
            {
                methodNode.Add("Description", description);
            }

            // Search for parameter/field docs
            var parametersMap = new YamlMappingNode();
            var fieldsMap = new YamlMappingNode();
            StringBuilder docBuilder = new StringBuilder();
            line = mdFileReader.ReadLine();

            void ParseSection(Match match, YamlMappingNode receivingMap)
            {
                string sectionName = match.Groups[1].Value;
                while ((line = mdFileReader.ReadLine()) is object)
                {
                    if (line.StartsWith('#'))
                    {
                        break;
                    }

                    docBuilder.AppendLine(line);
                }

                try
                {
                    receivingMap.Add(sectionName, docBuilder.ToString().Trim());
                }
                catch (ArgumentException)
                {
                }

                docBuilder.Clear();
            }

            while (line is object)
            {
                if (ParameterHeaderPattern.Match(line) is Match { Success: true } parameterMatch)
                {
                    ParseSection(parameterMatch, parametersMap);
                }
                else if (FieldHeaderPattern.Match(line) is Match { Success: true } fieldMatch)
                {
                    ParseSection(fieldMatch, fieldsMap);
                }
                else
                {
                    if (line is object && ReturnHeaderPattern.IsMatch(line))
                    {
                        break;
                    }

                    line = mdFileReader.ReadLine();
                }
            }

            if (parametersMap.Any())
            {
                methodNode.Add("Parameters", parametersMap);
            }

            if (fieldsMap.Any())
            {
                methodNode.Add("Fields", fieldsMap);
            }

            // Search for return value documentation
            while (line is object)
            {
                Match m = ReturnHeaderPattern.Match(line);
                if (m.Success)
                {
                    while ((line = mdFileReader.ReadLine()) is object)
                    {
                        if (line.StartsWith('#'))
                        {
                            break;
                        }

                        docBuilder.AppendLine(line);
                    }

                    methodNode.Add("ReturnValue", docBuilder.ToString().Trim());
                    docBuilder.Clear();
                    break;
                }
                else
                {
                    line = mdFileReader.ReadLine();
                }
            }

            return (properName, methodNode);
        }

        private class YamlSectionReader : TextReader
        {
            private readonly StreamReader fileReader;
            private bool firstLineRead;
            private bool lastLineRead;

            internal YamlSectionReader(StreamReader fileReader)
            {
                this.fileReader = fileReader;
            }

            public override string? ReadLine()
            {
                if (this.lastLineRead)
                {
                    return null;
                }

                if (!this.firstLineRead)
                {
                    Expect("---", this.fileReader.ReadLine());
                    this.firstLineRead = true;
                }

                string? line = this.fileReader.ReadLine();
                if (line == "---")
                {
                    this.lastLineRead = true;
                    return null;
                }

                return line;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.fileReader.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}

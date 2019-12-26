﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.CodeDom.Compiler;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MArchiveBatchTool.Psb.Writing;

namespace MArchiveBatchTool.Psb
{
    /// <summary>
    /// A collection of PSB analysis functions.
    /// </summary>
    static class Analysis
    {
        /// <summary>
        /// Generates a GraphViz DOT file for visualizing key name nodes.
        /// </summary>
        /// <param name="writer">The writer to write the DOT file to.</param>
        /// <param name="reader">The reader to read PSB from.</param>
        public static void GenerateNameGraphDot(TextWriter writer, PsbReader reader)
        {
            var nodes = reader.GenerateNameNodes();
            writer.WriteLine("digraph {");
            writer.WriteLine("node [shape=record]");
            writer.WriteLine("edge [dir=back]");
            foreach (var node in nodes.OrderBy(x => x.Key))
            {
                node.Value.WriteDot(writer);
            }
            writer.WriteLine("}");
        }

        /// <summary>
        /// Generates a debug trace of key name nodes.
        /// </summary>
        /// <param name="writer">The writer to write the trace to.</param>
        /// <param name="reader">The reader to read PSB from.</param>
        public static void GenerateNameRanges(TextWriter writer, PsbReader reader)
        {
            var nodes = reader.GenerateNameNodes();
            var root = nodes[0];
            IndentedTextWriter indentedWriter = new IndentedTextWriter(writer);
            WriteRange(indentedWriter, root);
        }

        static void WriteRange(IndentedTextWriter writer, NameNode node)
        {
            RegularNameNode regularNode = node as RegularNameNode;
            if (regularNode != null)
            {
                string line = $"{node.Index} {(char)node.Character} ";
                var regularChildren = regularNode.Children.Values.Where(x => x is RegularNameNode).OrderBy(x => x.Index);
                var terminator = regularNode.Children.Values.Where(x => x is TerminalNameNode).FirstOrDefault();

                if (terminator != null)
                {
                    line += $"[{terminator.Index}] ";
                }

                if (regularChildren.Count() > 0)
                {
                    var minIndex = regularChildren.First().Index;
                    var maxIndex = regularChildren.Last().Index;
                    if (minIndex == maxIndex)
                        line += $"<{minIndex}> ";
                    else
                        line += $"<{minIndex} {maxIndex}> ";
                }

                writer.WriteLine(line.Trim());

                ++writer.Indent;
                foreach (var child in regularNode.Children.Values)
                    WriteRange(writer, child);
                --writer.Indent;
            }
        }

        /// <summary>
        /// Generates a visualization of key names node range usage.
        /// </summary>
        /// <param name="writer">The writer to write the visualization to.</param>
        /// <param name="reader">The reader to read PSB from.</param>
        public static void GenerateRangeUsageVisualization(TextWriter writer, PsbReader reader)
        {
            RangeUsageAnalyzer analyzer = new RangeUsageAnalyzer();
            foreach (var node in reader.GenerateNameNodes().Values)
            {
                RegularNameNode regularNode = node as RegularNameNode;
                if (regularNode != null)
                {
                    var regularChildren = regularNode.Children.Values.Where(x => x is RegularNameNode).OrderBy(x => x.Index);
                    if (regularChildren.Count() > 0)
                    {
                        var minIndex = regularChildren.First().Index;
                        var maxIndex = regularChildren.Last().Index;
                        analyzer.AddRange(node.Index, minIndex, maxIndex, false);
                    }

                    var terminator = regularNode.Children.Values.Where(x => x is TerminalNameNode).FirstOrDefault();
                    if (terminator != null)
                    {
                        analyzer.AddRange(node.Index, terminator.Index, terminator.Index, false);
                    }
                }
                else
                {
                    analyzer.AddRange(node.Index, node.Index, node.Index, true);
                }
            }

            analyzer.OrderNodes();
            analyzer.WriteVisualization(writer);
        }

        /// <summary>
        /// Generates a key name tree and writes original and generated key names to <paramref name="writer"/>.
        /// </summary>
        /// <param name="writer">The writer to write the original and generated key names to.</param>
        /// <param name="reader">The reader to read PSB from.</param>
        /// <returns>Whether the original and generated key names match.</returns>
        public static bool TestKeyNamesGeneration(TextWriter writer, PsbReader reader)
        {
            KeyNamesGenerator generator = new KeyNamesGenerator(
                new StandardKeyNamesEncoder() { OutputDebug = false }, false);
            var keyNames = reader.KeyNames;
            for (uint i = 0; i < keyNames.Count; ++i)
            {
                writer.WriteLine(keyNames[i]);
                generator.AddString(keyNames[i]);
            }
            generator.Generate();
            writer.WriteLine("--------");
            var keyNamesNew = new PsbReader.KeyNamesReader(generator.ValueOffsets, generator.Tree, generator.Tails);
            for (uint i = 0; i < keyNamesNew.Count; ++i)
            {
                writer.WriteLine(keyNamesNew[i]);
            }

            for (uint i = 0; i < keyNames.Count; ++i)
            {
                if (keyNamesNew[i] != keyNames[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Tests reserialized and deserialized PSB is identical to original deserialization.
        /// </summary>
        /// <param name="reader">The initial PSB reader.</param>
        /// <param name="psbOutPath">The path to write serialized PSB to.</param>
        /// <param name="jsonWriter">The JSON writer to write newly deserialized PSB to.</param>
        /// <param name="debugWriter">Optional debug writer to write deserialization debug data to.</param>
        /// <returns>Whether the originally read PSB and newly serialized PSB are semantically identical.</returns>
        public static bool TestSerializeDeserialize(PsbReader reader, string psbOutPath, TextWriter jsonWriter, TextWriter debugWriter)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                PsbWriter writer = new PsbWriter(reader.Root, null)
                {
                    Version = reader.Version,
                    Optimize = true
                };
                writer.Write(ms);

                File.WriteAllBytes(psbOutPath, ms.ToArray());

                ms.Seek(0, SeekOrigin.Begin);
                PsbReader newReader = new PsbReader(ms, null, debugWriter);
                JsonTextWriter jtw = new JsonTextWriter(jsonWriter) { Formatting = Formatting.Indented };
                newReader.Root.WriteTo(jtw);
                return new JTokenEqualityComparer().Equals(reader.Root, newReader.Root);
            }
        }

        /// <summary>
        /// Tests serialized and deserialized PSB is identical to the original root.
        /// </summary>
        /// <param name="root">The token to serialize.</param>
        /// <param name="streamSource">The stream source for <see cref="JStream"/>s in <paramref name="root"/>.</param>
        /// <param name="psbOutPath">The path to write serialized PSB to.</param>
        /// <param name="jsonWriter">The JSON writer to write newly deserialized PSB to.</param>
        /// <param name="debugWriter">Optional debug writer to write deserialization debug data to.</param>
        /// <returns>Whether the originally read PSB and newly serialized PSB are semantically identical.</returns>
        public static bool TestSerializeDeserialize(JToken root, IPsbStreamSource streamSource, string psbOutPath, TextWriter jsonWriter, TextWriter debugWriter)
        {
            using (FileStream fs = File.Create(psbOutPath))
            {
                PsbWriter writer = new PsbWriter(root, streamSource)
                {
                    Version = 4,
                    Optimize = true
                };
                writer.Write(fs);
                fs.Flush();

                fs.Seek(0, SeekOrigin.Begin);
                PsbReader reader = new PsbReader(fs, null, debugWriter);
                reader.Root.WriteTo(new JsonTextWriter(jsonWriter) { Formatting = Formatting.Indented });

                return new JTokenEqualityComparer().Equals(root, reader.Root);
            }
        }
    }
}

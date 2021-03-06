﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MadsKristensen.EditorExtensions.Helpers;
using Microsoft.CSS.Core;

namespace MadsKristensen.EditorExtensions
{
    public static class LessCompiler
    {
        private static readonly Regex _endingCurlyBraces = new Regex(@"}\W*}|}", RegexOptions.Compiled);
        private static readonly Regex _linesStartingWithTwoSpaces = new Regex("(\n( *))", RegexOptions.Compiled);
        private static readonly string webEssentialsNodeDir = Path.Combine(Path.GetDirectoryName(typeof(LessCompiler).Assembly.Location), @"Resources\nodejs");
        private static readonly string lessCompiler = Path.Combine(webEssentialsNodeDir, @"node_modules\less\bin\lessc");
        private static readonly string node = Path.Combine(webEssentialsNodeDir, @"node.exe");
        private static readonly Regex errorParser = new Regex(@"^(.+) in (.+) on line (\d+), column (\d+):$", RegexOptions.Multiline);

        public static async Task<CompilerResult> Compile(string fileName, string targetFileName = null, string sourceMapRootPath = null)
        {
            string output = Path.GetTempFileName();
            string arguments = String.Format("--no-color --relative-urls \"{0}\" \"{1}\"", fileName, output);
            string fileNameWithoutPath = Path.GetFileName(fileName);
            string sourceMapArguments = (string.IsNullOrEmpty(sourceMapRootPath)) ? "" : String.Format("--source-map-rootpath=\"{0}\" ", sourceMapRootPath.Replace("\\", "/"));

            if (WESettings.GetBoolean(WESettings.Keys.LessSourceMaps))
                arguments = String.Format("--no-color --relative-urls {0}--source-map=\"{1}.map\" \"{2}\" \"{3}\"", sourceMapArguments, fileNameWithoutPath, fileName, output);

            ProcessStartInfo start = new ProcessStartInfo(String.Format("\"{0}\" \"{1}\"", (File.Exists(node)) ? node : "node", lessCompiler))
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(fileName),
                CreateNoWindow = true,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true
            };

            using (var process = await ExecuteAsync(start))
            {
                return ProcessResult(output, process, fileName, targetFileName);
            }
        }

        private static Task<Process> ExecuteAsync(ProcessStartInfo startInfo)
        {
            var process = Process.Start(startInfo);
            var processTaskCompletionSource = new TaskCompletionSource<Process>();

            process.EnableRaisingEvents = true;
            process.Exited += (s, e) => processTaskCompletionSource.TrySetResult(process);
            if (process.HasExited)
                processTaskCompletionSource.TrySetResult(process);
            return processTaskCompletionSource.Task;
        }

        private static CompilerResult ProcessResult(string output, Process process, string fileName, string targetFileName)
        {
            CompilerResult result = new CompilerResult(fileName);

            ProcessResult(output, process, result);

            if (result.IsSuccess)
            {
                // Inserts an empty row between each rule and replace two space indentation with 4 space indentation
                result.Result = _endingCurlyBraces.Replace(_linesStartingWithTwoSpaces.Replace(result.Result.Trim(), "$1$2"), "$&\n");

                // If the caller wants us to renormalize URLs to a different filename, do so.
                if (targetFileName != null && result.Result.IndexOf("url(", StringComparison.OrdinalIgnoreCase) > 0)
                {
                    try
                    {
                        result.Result = CssUrlNormalizer.NormalizeUrls(
                            tree: new CssParser().Parse(result.Result, true),
                            targetFile: targetFileName,
                            oldBasePath: fileName
                        );
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("An error occurred while normalizing generated paths in " + fileName + "\r\n" + ex);
                    }
                }
            }
            Logger.Log(Path.GetFileName(fileName) + " compiled");
            return result;
        }

        private static void ProcessResult(string outputFile, Process process, CompilerResult result)
        {
            if (!File.Exists(outputFile))
                throw new FileNotFoundException("LESS compiled output not found", outputFile);

            if (process.ExitCode == 0)
            {
                result.Result = File.ReadAllText(outputFile);
                result.IsSuccess = true;
            }
            else
            {
                using (StreamReader reader = process.StandardError)
                {
                    string error = reader.ReadToEnd();
                    Debug.WriteLine("LessCompiler Error: " + error);
                    result.Error = ParseError(error);
                }
            }

            File.Delete(outputFile);
        }

        private static CompilerError ParseError(string error)
        {
            var match = errorParser.Match(error);
            if (!match.Success)
            {
                Logger.Log("Unparseable LESS error: " + error);
                return new CompilerError { Message = error };
            }
            return new CompilerError
            {
                Message = match.Groups[1].Value,
                FileName = match.Groups[2].Value,
                Line = int.Parse(match.Groups[3].Value, CultureInfo.CurrentCulture),
                Column = int.Parse(match.Groups[4].Value, CultureInfo.CurrentCulture)
            };
        }
    }
}
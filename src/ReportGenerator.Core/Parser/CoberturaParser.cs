using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Common;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by Cobertura.
    /// </summary>
    internal class CoberturaParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(CoberturaParser));

        /// <summary>
        /// Regex to analyze if a class name represents a generic class.
        /// </summary>
        private static readonly Regex GenericClassRegex = new Regex("<.*>$", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private static readonly Regex LambdaMethodNameRegex = new Regex("<.+>.+__", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze if a method name is generated by compiler.
        /// </summary>
        private static readonly Regex CompilerGeneratedMethodNameRegex = new Regex(@"(?<ClassName>.+)/<(?<CompilerGeneratedName>.+)>.+__.+MoveNext\(\)$", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze the branch coverage of a line element.
        /// </summary>
        private static readonly Regex BranchCoverageRegex = new Regex("\\((?<NumberOfCoveredBranches>\\d+)/(?<NumberOfTotalBranches>\\d+)\\)$", RegexOptions.Compiled);

        /// <summary>
        /// Initializes a new instance of the <see cref="CoberturaParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal CoberturaParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report.</param>
        /// <returns>The parser result.</returns>
        public ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var modules = report.Descendants("package")
              .ToArray();

            var assemblyNames = modules
                .Select(m => m.Attribute("name").Value)
                .Distinct()
                .Where(a => this.AssemblyFilter.IsElementIncludedInReport(a))
                .OrderBy(a => a)
                .ToArray();

            foreach (var assemblyName in assemblyNames)
            {
                assemblies.Add(this.ProcessAssembly(modules, assemblyName));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), true, this.ToString());

            foreach (var sourceElement in report.Elements("sources").Elements("source"))
            {
                result.AddSourceDirectory(sourceElement.Value);
            }

            try
            {
                if (report.Element("sources").Parent.Attribute("timestamp") != null)
                {
                    DateTime timeStamp = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                    timeStamp = timeStamp.AddSeconds(double.Parse(report.Element("sources").Parent.Attribute("timestamp").Value)).ToLocalTime();

                    result.MinimumTimeStamp = timeStamp;
                    result.MaximumTimeStamp = timeStamp;
                }
            }
            catch (Exception)
            {
                // Ignore since timestamp is not relevant. If timestamp is missing or in wrong format the information is just missing in the report(s)
            }

            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(XElement[] modules, string assemblyName)
        {
            Logger.DebugFormat(Resources.CurrentAssembly, assemblyName);

            var classNames = modules
                .Where(m => m.Attribute("name").Value.Equals(assemblyName))
                .Elements("classes")
                .Elements("class")
                .Select(c =>
                {
                    string fullname = c.Attribute("name").Value;
                    int nestedClassSeparatorIndex = fullname.IndexOf('/');
                    return nestedClassSeparatorIndex > -1 ? fullname.Substring(0, nestedClassSeparatorIndex) : fullname;
                })
                .Where(name => !name.Contains("$") && !name.Contains("<>") && !name.Contains(">d") && !name.Contains(">g"))
                .Distinct()
                .Where(c => this.ClassFilter.IsElementIncludedInReport(c))
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => this.ProcessClass(modules, assembly, className));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        private void ProcessClass(XElement[] modules, Assembly assembly, string className)
        {
            var files = modules
                .Where(m => m.Attribute("name").Value.Equals(assembly.Name))
                .Elements("classes")
                .Elements("class")
                .Where(c => c.Attribute("name").Value.Equals(className)
                    || c.Attribute("name").Value.StartsWith(className + "$", StringComparison.Ordinal)
                    || c.Attribute("name").Value.StartsWith(className + "/", StringComparison.Ordinal))
                .Select(c => c.Attribute("filename").Value)
                .Distinct()
                .ToArray();

            var filteredFiles = files
                .Where(f => this.FileFilter.IsElementIncludedInReport(f))
                .ToArray();

            // If all files are removed by filters, then the whole class is omitted
            if ((files.Length == 0 && !this.FileFilter.HasCustomFilters) || filteredFiles.Length > 0)
            {
                var @class = new Class(className, assembly);

                foreach (var file in filteredFiles)
                {
                    @class.AddFile(ProcessFile(modules, @class, file));
                }

                assembly.AddClass(@class);
            }
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="modules">The modules.</param>
        /// <param name="class">The class.</param>
        /// <param name="filePath">The file path.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private static CodeFile ProcessFile(XElement[] modules, Class @class, string filePath)
        {
            var classes = modules
                .Where(m => m.Attribute("name").Value.Equals(@class.Assembly.Name))
                .Elements("classes")
                .Elements("class")
                .Where(c => c.Attribute("name").Value.Equals(@class.Name)
                            || c.Attribute("name").Value.StartsWith(@class.Name + "$", StringComparison.Ordinal)
                            || c.Attribute("name").Value.StartsWith(@class.Name + "/", StringComparison.Ordinal)
                            || c.Attribute("name").Value.StartsWith(@class.Name + ".", StringComparison.Ordinal))
                .Where(c => c.Attribute("filename").Value.Equals(filePath))
                .ToArray();

            var lines = classes.Elements("lines")
                .Elements("line")
                .ToArray();

            var linesOfFile = lines
                .Select(line => new
                {
                    LineNumber = int.Parse(line.Attribute("number").Value, CultureInfo.InvariantCulture),
                    Visits = line.Attribute("hits").Value.ParseLargeInteger()
                })
                .OrderBy(seqpnt => seqpnt.LineNumber)
                .ToArray();

            var branches = GetBranches(lines);

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (linesOfFile.Length > 0)
            {
                coverage = new int[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];
                lineVisitStatus = new LineVisitStatus[linesOfFile[linesOfFile.LongLength - 1].LineNumber + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var line in linesOfFile)
                {
                    coverage[line.LineNumber] = line.Visits;

                    bool partiallyCovered = false;

                    if (branches.TryGetValue(line.LineNumber, out ICollection<Branch> branchesOfLine))
                    {
                        partiallyCovered = branchesOfLine.Any(b => b.BranchVisits == 0);
                    }

                    LineVisitStatus statusOfLine = line.Visits > 0 ? (partiallyCovered ? LineVisitStatus.PartiallyCovered : LineVisitStatus.Covered) : LineVisitStatus.NotCovered;
                    lineVisitStatus[line.LineNumber] = statusOfLine;
                }
            }

            var methodsOfFile = classes
                .Elements("methods")
                .Elements("method")
                .ToArray();

            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus, branches);

            SetMethodMetrics(codeFile, methodsOfFile);
            SetCodeElements(codeFile, methodsOfFile);

            return codeFile;
        }

        /// <summary>
        /// Extracts the metrics from the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetMethodMetrics(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                string fullName = method.Attribute("name").Value + method.Attribute("signature").Value;
                fullName = ExtractMethodName(fullName, method.Parent.Parent.Attribute("name").Value);

                if (fullName.Contains("__") && LambdaMethodNameRegex.IsMatch(fullName))
                {
                    continue;
                }

                string shortName = GetShortMethodName(fullName);

                var metrics = new List<Metric>();

                var lineRate = method.Attribute("line-rate");

                if (lineRate != null)
                {
                    decimal? value = null;

                    if (!"NaN".Equals(lineRate.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        value = Math.Round(100 * decimal.Parse(lineRate.Value.Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero);
                    }

                    metrics.Add(new Metric(
                        ReportResources.Coverage,
                        ParserBase.CodeCoverageUri,
                        MetricType.CoveragePercentual,
                        value));
                }

                var branchRate = method.Attribute("branch-rate");

                if (branchRate != null)
                {
                    decimal? value = null;

                    if (!"NaN".Equals(branchRate.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        value = Math.Round(100 * decimal.Parse(branchRate.Value.Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero);
                    }

                    metrics.Add(new Metric(
                        ReportResources.BranchCoverage,
                        ParserBase.CodeCoverageUri,
                        MetricType.CoveragePercentual,
                        value));
                }

                var cyclomaticComplexityAttribute = method.Attribute("complexity");

                if (cyclomaticComplexityAttribute != null)
                {
                    decimal? value = null;

                    if (!"NaN".Equals(cyclomaticComplexityAttribute.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        value = Math.Round(decimal.Parse(cyclomaticComplexityAttribute.Value.Replace(',', '.'), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture), 2, MidpointRounding.AwayFromZero);
                    }

                    metrics.Insert(
                        0,
                        new Metric(
                        ReportResources.CyclomaticComplexity,
                        ParserBase.CyclomaticComplexityUri,
                        MetricType.CodeQuality,
                        value,
                        MetricMergeOrder.LowerIsBetter));
                }

                var methodMetric = new MethodMetric(fullName, shortName, metrics);

                var line = method
                    .Elements("lines")
                    .Elements("line")
                    .FirstOrDefault();

                if (line != null)
                {
                    methodMetric.Line = int.Parse(line.Attribute("number").Value, CultureInfo.InvariantCulture);
                }

                codeFile.AddMethodMetric(methodMetric);
            }
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                string methodName = method.Attribute("name").Value + method.Attribute("signature").Value;
                methodName = ExtractMethodName(methodName, method.Parent.Parent.Attribute("name").Value);

                if (methodName.Contains("__") && LambdaMethodNameRegex.IsMatch(methodName))
                {
                    continue;
                }

                var lines = method.Elements("lines")
                    .Elements("line");

                if (lines.Any())
                {
                    int firstLine = int.Parse(lines.First().Attribute("number").Value, CultureInfo.InvariantCulture);
                    int lastLine = int.Parse(lines.Last().Attribute("number").Value, CultureInfo.InvariantCulture);

                    codeFile.AddCodeElement(new CodeElement(
                        methodName,
                        CodeElementType.Method,
                        firstLine,
                        lastLine,
                        codeFile.CoverageQuota(firstLine, lastLine)));
                }
            }
        }

        /// <summary>
        /// Gets the branches by line number.
        /// </summary>
        /// <param name="lines">The lines.</param>
        /// <returns>The branches by line number.</returns>
        private static Dictionary<int, ICollection<Branch>> GetBranches(IEnumerable<XElement> lines)
        {
            var result = new Dictionary<int, ICollection<Branch>>();

            foreach (var line in lines)
            {
                if (line.Attribute("condition-coverage") == null
                    || line.Attribute("branch") == null
                    || !line.Attribute("branch").Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = BranchCoverageRegex.Match(line.Attribute("condition-coverage").Value);

                if (match.Success)
                {
                    int lineNumber = int.Parse(line.Attribute("number").Value, CultureInfo.InvariantCulture);

                    int numberOfCoveredBranches = int.Parse(match.Groups["NumberOfCoveredBranches"].Value, CultureInfo.InvariantCulture);
                    int numberOfTotalBranches = int.Parse(match.Groups["NumberOfTotalBranches"].Value, CultureInfo.InvariantCulture);

                    var branches = new HashSet<Branch>();

                    for (int i = 0; i < numberOfTotalBranches; i++)
                    {
                        string identifier = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}_{1}",
                            lineNumber,
                            i);

                        branches.Add(new Branch(i < numberOfCoveredBranches ? 1 : 0, identifier));
                    }

                    /* If cobertura file is merged from different files, a class and therefore each line can exist several times.
                     * The best result is used. */
                    if (result.TryGetValue(lineNumber, out ICollection<Branch> existingBranches))
                    {
                        if (numberOfCoveredBranches > existingBranches.Count(b => b.BranchVisits == 1))
                        {
                            result[lineNumber] = branches;
                        }
                    }
                    else
                    {
                        result.Add(lineNumber, branches);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts the method name. For async methods the original name is returned.
        /// </summary>
        /// <param name="methodName">The full method name.</param>
        /// <param name="className">The name of the class.</param>
        /// <returns>The method name.</returns>
        private static string ExtractMethodName(string methodName, string className)
        {
            // Quick check before expensive regex is called
            if (methodName.EndsWith("MoveNext()"))
            {
                Match match = CompilerGeneratedMethodNameRegex.Match(className + methodName);

                if (match.Success)
                {
                    methodName = match.Groups["CompilerGeneratedName"].Value + "()";
                }
            }

            return methodName;
        }

        private static string GetShortMethodName(string fullName)
        {
            int indexOpen = fullName.IndexOf('(');

            if (indexOpen <= 0)
            {
                return fullName;
            }

            int indexClose = fullName.IndexOf(')');
            string signature = indexClose - indexOpen > 1 ? "(...)" : "()";

            return $"{fullName.Substring(0, indexOpen)}{signature}";
        }
    }
}

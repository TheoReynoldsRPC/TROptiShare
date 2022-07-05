using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TycheSupport;
using TycheSupport.Primitives;
using TycheSupport.Reports;
using TycheSupport.TycheSIMD.TycheDoubleVectors;
using TycheSupport.TycheSIMD.VectorStorage;
using TycheSupport.TycheVariables;
using TycheSupport.VisualStudio;

namespace GetMaxAndPositions
{
    /// <summary>
    /// Output the max value and position of the max value in a stochastic 2D matrix, per scenario.<para/>
	/// Inputs:
	///   1. InputVariable - stochastic 2D matrix. Matrices may be jagged. "Blanks" allowed.
	///   2. FindOption - "First" or "Last" position if multiple match. This acts on outer dimension first.<para/>
	/// Outputs:
	///   1. Stochastic 1D vector with outer dimension index, inner dimension index and max value entries. Note: These are all returned as doubles and may contain "Blanks".
    ///      This can used to dereference the 'InputVariable' to pick out that entry to get 'ValueToFind'.
	///      e.g. PositionAndMaxVector = GetPositions(InputVariable, "First");
	///           ValueToFindCheck = InputVariable[PositionAndMaxVector[0],PositionAndMaxVector[1]];
    ///           MaxValues = PositionAndMaxVector[2];<para/>
    /// Notes:
    ///   1. Can't return string index of outer dimension, as inner dimension uses integer position only. UDF's can only return one variable and types must match.
    /// </summary>
    public sealed class GetMaxAndPositions : ITycheCSharpOperation
    {
        private const string OutputDimensionName = "OuterInnerMax";

        /// <summary>
        /// Access to the underlying vector pointer for performance.
        /// </summary>
        private static readonly FieldInfo fPtrFieldInfo = typeof(TycheSIMDVector).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(double*));

        /// <summary>
        /// Not used.
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// The minimum number of input variables for this node.
        /// </summary>
        public int MinInputs => 2;

        /// <summary>
        /// The maximum number of input variables for this node.
        /// </summary>
        public int MaxInputs => 2;

        /// <summary>
        /// The expected number of output variables for this node.
        /// </summary>
        public int NumOutputs => 1;

        /// <summary>
        /// Determine the output types, given the input types.
        /// </summary>
        /// <param name="inputs">The input types passed to this node (one entry in the list for each input variable).</param>
        /// <param name="reports">Storage for any warnings or errors that are produced during execution of this operation</param>
        /// <param name="outputTypes">The output types returned from this node (should contain one entry for each output variable).</param>
        /// <returns>
        /// True on success, otherwise false.
        /// </returns>
        public bool DetermineOutputTypes(List<TycheTypeData> inputs, TycheReports reports, List<TycheTypeData> outputTypes)
        {
			if(inputs.Count != MinInputs)
			{
				reports.AddErrorWithoutLocation($"Unexpected number of inputs. Expected '{MinInputs}'. Actual '{inputs.Count}'.");
                return false;
			}

            TycheTypeData inputVariable = inputs[0];
            TycheTypeData findOption = inputs[1];

            if(!inputVariable.DataType.IsNumber())
            {
                reports.AddErrorWithoutLocation($"The 'input' variable must be of type integer or double. Actual type '{inputVariable.DataType}'.");
                return false;
            }

            if(findOption.DataType != TycheDataType.String)
            {
                reports.AddErrorWithoutLocation($"The 'find option' variable must be of type string. Actual type '{findOption.DataType}'.");
                return false;
            }

            if(!inputVariable.IsMatrix2D)
            {
                reports.AddErrorWithoutLocation($"The 'input' variable must be a 2D matrix. Actual dimension count '{inputVariable.NumDimensions}'.");
                return false;
            }

            if(!findOption.IsScalar)
            {
                reports.AddErrorWithoutLocation($"The 'find option' variable must be a scalar. Actual dimension count '{findOption.NumDimensions}'.");
                return false;
            }

            // Return a 1D vector that will hold the position coordinates and max value for each scenario.
            outputTypes.Add(TycheTypeData.CreateVectorTycheType(TycheDataType.Double, OutputDimensionName));

            return true;
        }

        /// <summary>
        /// Perform the operation.
        /// </summary>
        /// <param name="inputs">Variables that have already been executed earlier in the model, and are provided as inputs to this operation.</param>
        /// <param name="factory">A factory for constructing variables that will be output from this operation.</param>
        /// <param name="reports">Storage for any warnings or errors that are produced during execution of this operation.</param>
        /// <param name="outputs">Variables that we make using the given factory, and set with the desired values.</param>
        /// <returns>
        /// True on success, otherwise false.
        /// </returns>
        public bool Execute(List<TycheVariable> inputs, List<TycheVariable> outputs, ITycheVariableFactory factory, TycheReports reports)
        {
            TycheVariable inputVariable = inputs[0];
            TycheVariable findOptionVariable = inputs[1];

            // Validate the inputs.
            if(inputVariable.IsDeterministic)
            {
                reports.AddErrorWithoutLocation("The 'input' variable must be stochastic.");
                return false;
            }

            if(findOptionVariable.IsStochastic)
            {
                reports.AddErrorWithoutLocation("The 'find option' variable must be deterministic.");
                return false;
            }

            FindOption findOption;
            if(!Enum.TryParse(findOptionVariable.DeterministicValueAsString, out findOption))
            {
                reports.AddErrorWithoutLocation($"The 'find option' variable must be one of '{FindOption.First}' or '{FindOption.Last}'.");
                return false;
            }

            // Create a 1D vector that will hold the position coordinates and max value for each scenario.
            TycheVariable outputVariable = factory.CreateVariable(TycheDataType.Double, new[] { 3 }, new[] { OutputDimensionName }, inputVariable.NumScenarios);
            if(outputVariable == null)
            {
                return false;
            }

            outputs.Add(outputVariable);

            // Find the positions.
            if(!TryGetPositions(inputVariable, outputVariable, findOption, reports))
            {
                return false;
            }

            // Add input and output to same dependency group.
            factory.Globals.DependencyManager.MergeVariableDependencyGroups(inputVariable, outputVariable);

            return true;
        }

        /// <summary>
        /// Calculate the positions.
        /// </summary>
        private static unsafe bool TryGetPositions(TycheVariable inputVariable, TycheVariable outputVariable, FindOption findOption, TycheReports reports)
        {
            // TODO: Handle "NaNs", zero-length variables etc?
            // TODO: Multi-thread here or via Tyche Managed Threads? Thread static array results? Doesn't help?
            // TODO: double == tolerance?
            // TODO: If we had exactly equiv sparse vectors we could optimise the sparse case.

            using(var localFactory = new TycheLocalVectorFactory(inputVariable.Globals.VectorFactory))
            {
                // Gather the stochastic values to pass to the local vector factory and thus keep "alive".
                int numRows = inputVariable.NumSubVariables;
                var inputRowVectors = new List<List<ITycheDoubleVector>>(numRows);
                for(int row = 0; row < numRows; ++row)
                {
                    TycheVariable rowVariable = inputVariable[row];
                    var inputColumnVectors = new List<ITycheDoubleVector>(rowVariable.NumSubVariables);
                    inputRowVectors.Add(inputColumnVectors);
                    for(int column = 0; column < rowVariable.NumSubVariables; ++column)
                    {
                        var columnVector = rowVariable[column];
                        localFactory.AddVector(columnVector.StochasticValues);

                        // As we access raw pointers we need to operate on "dense" vectors.
                        ITycheDoubleVector vectorToAdd = columnVector.StochasticValues.IsDense ? columnVector.StochasticValues : columnVector.StochasticValues.ToDense();

                        inputColumnVectors.Add(vectorToAdd);
                    }
                }

                // Find the positions (per scenario).
                int numScenarios = inputVariable.NumScenarios;
                var rowResults = new double[numScenarios];
                var columnResults = new double[numScenarios];
                var maxValueResults = new double[numScenarios];
                for(int row = 0; row < numRows; ++row)
                {
                    var rowVectors = inputRowVectors[row];
                    for(int column = 0; column < rowVectors.Count; ++column)
                    {
                        ITycheDoubleVector columnVector = rowVectors[column];

                        // Note: Like 'MultiColumnIndexOf' managed operations are faster than SIMD operations.
                        // Infact we need to access the raw pointers to avoid coping data from SIMD (C++) to Managed (C#) layer which is main bottleneck.
                        var columnArray = (double*)Pointer.Unbox(fPtrFieldInfo.GetValue(columnVector.GetReadOnlyNativeVector()));

                        bool firstTime = row == 0 && column == 0;
                        for(int scenario = 0; scenario < numScenarios; ++scenario)
                        {
                            if(firstTime)
                            {
                                // Initialise the positions to not found ("Blank").
                                rowResults[scenario] = TycheConstants.BlankValue;
                                columnResults[scenario] = TycheConstants.BlankValue;
                                maxValueResults[scenario] = TycheConstants.BlankValue;
                            }

                            double columnValue = columnArray[scenario];
                            if(columnValue == TycheConstants.BlankValue)
                            {
                                continue;
                            }

                            if(findOption == FindOption.First)
                            {
                                if(columnValue > maxValueResults[scenario])
                                {
                                    rowResults[scenario] = row;
                                    columnResults[scenario] = column;
                                    maxValueResults[scenario] = columnValue;
                                }
                            }
                            else
                            {
                                if(columnValue >= maxValueResults[scenario])
                                {
                                    rowResults[scenario] = row;
                                    columnResults[scenario] = column;
                                    maxValueResults[scenario] = columnValue;
                                }
                            }
                        }
                    }
                }

                // Populate the results. Note: This is a bottleneck.
                if(!outputVariable[0].StochasticValues.SetFrom(rowResults))
                {
                    reports.AddErrorWithoutLocation("Failed to set the outer dimension index (row) results.");
                    return false;
                }

                if(!outputVariable[1].StochasticValues.SetFrom(columnResults))
                {
                    reports.AddErrorWithoutLocation("Failed to set the inner dimension index (column) results.");
                    return false;
                }

                if(!outputVariable[2].StochasticValues.SetFrom(maxValueResults))
                {
                    reports.AddErrorWithoutLocation("Failed to set the max value results.");
                    return false;
                }

                return true;
            }
        }
    }
}
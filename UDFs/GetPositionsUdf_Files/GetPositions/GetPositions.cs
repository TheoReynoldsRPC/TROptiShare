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

namespace GetPositions
{
    /// <summary>
    /// Output the position of a particular entry in a stochastic 2D matrix, per scenario.<para/>
	/// Inputs:
	///   1. InputVariable - stochastic 2D matrix. Matrices may be jagged. Blanks allowed.
	///   2. ValueToFind - stochastic scalar variable. Blanks allowed.
	///   3. FindOption - "First" or "Last" position if multiple match. This acts on outer dimension first.<para/>
	/// Outputs:
	///   1. Stochastic 1D vector with x and y rows. This can used to dereference the 'InputVariable' to pick out that entry to get 'ValueToFind'.
	///      e.g. PositionVector = GetPositions(InputVariable, ValueToFind, "First");
	///           ValueToFindCheck = InputVariable[PositionVector[0],PositionVector[1]];<para/>
    /// Notes:
    ///   1. Pass "Blank" for valueToFind, instead of non-existing value, to speed up processing.
    ///   2. Can't return string index of outer dimension, as inner dimension uses integer position only. UDF's can only return one variable and types must match.
    /// </summary>
    public sealed class GetPositions : ITycheCSharpOperation
    {
        private const string OutputDimensionName = "coordinate";

        private static readonly FieldInfo fPtrFieldInfo = typeof(TycheSIMDVector).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(double*));

        /// <summary>
        /// Not used.
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// The minimum number of input variables for this node.
        /// </summary>
        public int MinInputs => 3;

        /// <summary>
        /// The maximum number of input variables for this node.
        /// </summary>
        public int MaxInputs => 3;

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
            TycheTypeData valueToFind = inputs[1];
            TycheTypeData findOption = inputs[2];

            if(!inputVariable.DataType.IsNumber())
            {
                reports.AddErrorWithoutLocation($"The 'input' variable must be of type integer or double. Actual type '{inputVariable.DataType}'.");
                return false;
            }

            if(!valueToFind.DataType.IsNumber())
            {
                reports.AddErrorWithoutLocation($"The 'value to find' variable must be of type integer or double. Actual type '{inputVariable.DataType}'.");
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

            if(!valueToFind.IsScalar)
            {
                reports.AddErrorWithoutLocation($"The 'value to find' variable must be a scalar. Actual dimension count '{valueToFind.NumDimensions}'.");
                return false;
            }

            if(!findOption.IsScalar)
            {
                reports.AddErrorWithoutLocation($"The 'find option' variable must be a scalar. Actual dimension count '{findOption.NumDimensions}'.");
                return false;
            }

            // Return a 1D vector that will hold the position coordinates for each scenario.
            outputTypes.Add(TycheTypeData.CreateVectorTycheType(TycheDataType.Integer, OutputDimensionName));

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
            TycheVariable valueToFindVariable = inputs[1];
            TycheVariable findOptionVariable = inputs[2];

            // Validate the inputs.
            if(inputVariable.IsDeterministic)
            {
                reports.AddErrorWithoutLocation("The 'input' variable must be stochastic.");
                return false;
            }

            if(valueToFindVariable.IsDeterministic)
            {
                reports.AddErrorWithoutLocation("The 'value to find' variable must be stochastic.");
                return false;
            }

            if(findOptionVariable.IsStochastic)
            {
                reports.AddErrorWithoutLocation("The 'find option' variable must be deterministic.");
                return false;
            }

            if(inputVariable.NumScenarios != valueToFindVariable.NumScenarios)
            {
                reports.AddErrorWithoutLocation($"The 'input' and 'value to find' scenario counts must match. Actual counts '{inputVariable.NumScenarios}' '{valueToFindVariable.NumScenarios}'.");
                return false;
            }

            FindOption findOption;
            if(!Enum.TryParse(findOptionVariable.DeterministicValueAsString, out findOption))
            {
                reports.AddErrorWithoutLocation($"The 'find option' variable must be one of '{FindOption.First}' or '{FindOption.Last}'.");
                return false;
            }

            // Create a 1D vector that will hold the position coordinates for each scenario.
            TycheVariable outputVariable = factory.CreateVariable(TycheDataType.Integer, new[] { 2 }, new[] { OutputDimensionName }, inputVariable.NumScenarios, reports);
            if(outputVariable == null)
            {
                return false;
            }

            outputs.Add(outputVariable);

            // Find the positions.
            if(!TryGetPositions(inputVariable, valueToFindVariable, outputVariable, findOption, reports))
            {
                return false;
            }

            // Add input and output to same dependency group.
            factory.Globals.DependencyManager.MergeVariableDependencyGroups(inputVariable, valueToFindVariable, outputVariable);

            return true;
        }

        /// <summary>
        /// Calculate the positions.
        /// </summary>
        private static unsafe bool TryGetPositions(TycheVariable inputVariable, TycheVariable valueToFindVariable, TycheVariable outputVariable, FindOption findOption, TycheReports reports)
        {
            // TODO: Handle "NaNs" etc?
            // TODO: Perform "max" calc in here too? But need to pass out and its a double not an integer.
            // TODO: Multi-thread here or via Tyche Managed Threads? Thread static array results? Doesn't help.
            // TODO: double == tolerance?

            // Gather the stochastic values to pass to the local vector factory and thus keep "alive".
            int numRows = inputVariable.NumSubVariables;
            var inputRowVectors = new List<IList<ITycheDoubleVector>>(numRows);
            for(int row = 0; row < numRows; ++row)
            {
                TycheVariable rowVariable = inputVariable[row];
                var inputColumnVectors = new List<ITycheDoubleVector>(rowVariable.NumSubVariables);
                inputRowVectors.Add(inputColumnVectors);
                for(int column = 0; column < rowVariable.NumSubVariables; ++column)
                {
                    var columnVector = rowVariable[column];
                    if(!columnVector.StochasticValues.IsDense)
                    {
                        // As we access raw pointers.
                        throw new NotSupportedException("The 'input' variable must only contain dense vectors.");
                    }

                    inputColumnVectors.Add(columnVector.StochasticValues);
                }
            }

            if(!valueToFindVariable.StochasticValues.IsDense)
            {
                // As we access raw pointers.
                throw new NotSupportedException("The 'value to find' variable must be a dense vector.");
            }

            using(var localFactory = new TycheLocalVectorFactory(inputRowVectors))
            {
                localFactory.AddVector(valueToFindVariable.StochasticValues);

                // Note: Like 'MultiColumnIndexOf' managed operations are faster than SIMD operations.
                // Infact we need to access the raw pointers to avoid coping data from SIMD (C++) to Managed (C#) layer which is main bottleneck.
                var valueToFindArray = (double*)Pointer.Unbox(fPtrFieldInfo.GetValue(valueToFindVariable.StochasticValues.Storage.SIMDVector));

                // Find the positions (per scenario).
                int numScenarios = valueToFindVariable.NumScenarios;
                var rowResults = new double[numScenarios];
                var columnResults = new double[numScenarios];
                for(int row = 0; row < numRows; ++row)
                {
                    var rowVectors = inputRowVectors[row];
                    for(int column = 0; column < rowVectors.Count; ++column)
                    {
                        ITycheDoubleVector columnVector = rowVectors[column];
                        var columnArray = (double*)Pointer.Unbox(fPtrFieldInfo.GetValue(columnVector.Storage.SIMDVector));
                        bool firstTime = row == 0 && column == 0;
                        for(int scenario = 0; scenario < numScenarios; ++scenario)
                        {
                            if(firstTime)
                            {
                                // Initialise the positions to not found ("Blank").
                                rowResults[scenario] = TycheConstants.BlankValue;
                                columnResults[scenario] = TycheConstants.BlankValue;
                            }
                            else if(findOption == FindOption.First && rowResults[scenario] != TycheConstants.BlankValue)
                            {
                                // Already found position for this matrix/scenario.
                                // TODO: Might be nice to exclude this matrix once position has been found.
                                continue;
                            }

                            double valueToFind = valueToFindArray[scenario];
                            if(valueToFind == TycheConstants.BlankValue)
                            {
                                // Note: Wee Shen passes 0 instead of "Blank". I assume that isn't a match....
                                // TODO: Might be nice if we could exclude this matrix if nothing to find.
                                continue;
                            }
                            else if(columnArray[scenario] == valueToFind)
                            {
                                rowResults[scenario] = row;
                                columnResults[scenario] = column;
                            }
                        }
                    }
                }

                // Populate the results. Note: This is a bottleneck.
                if(!outputVariable[0].StochasticValues.SetFrom(rowResults))
                {
                    reports.AddErrorWithoutLocation("Failed to set the row cordinate results.");
                    return false;
                }

                if(!outputVariable[1].StochasticValues.SetFrom(columnResults))
                {
                    reports.AddErrorWithoutLocation("Failed to set the column cordinate results.");
                    return false;
                }

                return true;
            }
        }
    }
}
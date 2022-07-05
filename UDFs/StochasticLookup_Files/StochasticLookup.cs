using System;
using System.Collections.Generic;
using System.Linq;
using Tyche.API.CSharp;

namespace StochasticLookup
{
    public class StochasticLookup : ICSharpOperation
    {
        public ICSharpOperationMetadata Metadata { get; } = new CSharpOperationMetadata()
        {
            MinInputs = 3,
            MaxInputs = 3,
            NumOutputs = 1
        };

        /// <summary>
        /// Determine the output types, given the input types.
        /// </summary>
        public bool Resolve(IResolveParameters parameters)
        {
            if(!parameters.InputTypeData.ValidateAtIndex(0, TypeIs.AnInteger & ShapeIs.AVector))
            {
                return false;
            }

            var values = parameters.InputTypeData[1];
            var defaultValue = parameters.InputTypeData[2];

            if(defaultValue.DataType != values.DataType && (defaultValue.DataType != DataType.Integer || values.DataType != DataType.Double))
            {
                parameters.ReportLogger.AddError("The datatype of the default value should match the data type of the values.");
                return false;
            }

            // Our output type is the same as the values type.
            parameters.OutputTypeData.Add(values);
            return true;
        }

        /// <summary>
        /// Perform the operation.
        /// </summary>
        public bool Execute(IExecuteParameters parameters)
        {
            var labels = parameters.InputVariables[0];
            var values = parameters.InputVariables[1];
            var defaultValue = parameters.InputVariables[2];

            if(!parameters.InputVariables.ValidateAtIndex(2, VariableIs.Deterministic))
            {
                return false;
            }

            if(labels.SubVariables.Count != values.SubVariables.Count)
            {
                parameters.ReportLogger.AddError("The length of the labels vector should match the length of the values vector.");
                return false;
            }

            if(labels.SubVariables.Count == 0)
            {
                // Just re-use the values variable in this case.
                parameters.OutputVariables.Add(values);
                return true;
            }

            int numScenarios = Math.Max(labels.NumScenarios, values.NumScenarios);

            int maxLabelValue = (int)labels.SubVariables.Max(scalar => scalar.StochasticValue.Max());
            maxLabelValue = Math.Max(maxLabelValue, -1);
            IMutableStochasticValue[] stochasticValues = new IMutableStochasticValue[maxLabelValue + 1];
            for(int i = 0; i < stochasticValues.Length; ++i)
            {
                stochasticValues[i] = parameters.StochasticValueFactory.Create(numScenarios, defaultValue.DeterministicValue);
            }

            // Initialise the result to contain the default value.
            int labelsCount = labels.NumScenarios;
            int valuesCount = values.NumScenarios;

            // Loop over the variables and indices and update the result.
            for(int variableIndex = 0; variableIndex < labels.SubVariables.Count; variableIndex++)
            {
                var labelsVector = labels.SubVariables[variableIndex].StochasticValue;
                var valuesVector = values.SubVariables[variableIndex].StochasticValue;
                for(int scenarioIndex = 0; scenarioIndex < numScenarios; scenarioIndex++)
                {
                    double label = labelsVector[Math.Min(scenarioIndex, labelsCount)];
                    if(label == Constants.BlankValue)
                    {
                        continue;
                    }

                    int labelIndex = (int)label;
                    double value = valuesVector[Math.Min(scenarioIndex, valuesCount)];
                    stochasticValues[labelIndex][scenarioIndex] = value;
                }
            }

            var result = parameters.VariableFactory.Vector.Create(values.DimensionNames[0], values.Type, stochasticValues);
            parameters.OutputVariables.Add(result);
            return true;
        }
    }
}
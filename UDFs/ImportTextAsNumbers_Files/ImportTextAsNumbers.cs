using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Tyche.API.CSharp;

// This namespace contains examples of classes written using the Tyche C# UDF API. These examples may be deleted or adapted as
// desired.
namespace ImportTextAsNumbers
{
    /// <summary>
    /// This file contains ImportTextAsNumbers function
    /// </summary>
    /// <remarks>
	/// In Tyche this function is coded as like this
	/// Result = ImportTextAsNumbers(Scalar_String,Scalar_String);
    /// Input1: Filepath
    /// Input2: Deliminator
	/// Output: Stochastic Indexed Vector of Doubles (first row is treated as headers, and each subsequent row in data is treated as a simulation)
    /// </remarks>
    public sealed class ImportTextAsNumbers : ICSharpOperation
    {
        /// <summary>
        /// The name of the dimension of the returned variable.
        /// </summary>
        private const string DimensionName = "Column";
        
        /// <summary>
        /// Gets an object which describes the basic structure of this C# operation node e.g. number of inputs, number of outputs, etc.
        /// </summary>
        public ICSharpOperationMetadata Metadata => new CSharpOperationMetadata
        {
            MinInputs = 2,
            MaxInputs = 2,
            NumOutputs = 1,
            IsPure = true
        };

        /// <summary>
        /// Determine the output types, given the input types.
        /// </summary>
        public bool Resolve(IResolveParameters parameters)
        {
            // Validation of two inputs can be performed by using the or operator.
            if(!parameters.InputTypeData.ValidateAtIndex(0, TypeIs.AString & ShapeIs.AScalar) |
               !parameters.InputTypeData.ValidateAtIndex(1, TypeIs.AString & ShapeIs.AScalar))
            {
                return false;
            }

            // The output type data is defined to have a single indexed dimension (DimensionName defined as a const) and always a double.
            parameters.OutputTypeData.Add(parameters.TypeDetailsFactory.Create(DataType.Double, new[] { new DimensionDetails(DimensionName, hasIndexer: true) }));
            return true;
        }

        /// <summary>
        /// Perform the operation.
        /// </summary>
        public bool Execute(IExecuteParameters parameters)
        {
			// only deterministic inputs allowed
            if(!parameters.InputVariables.ValidateAtIndex(0, VariableIs.Deterministic) || !parameters.InputVariables.ValidateAtIndex(1, VariableIs.Deterministic))
            {
                return false;
            }

            string filepath = parameters.InputVariables[0].DeterministicValueAsString;
            string deliminator = parameters.InputVariables[1].DeterministicValueAsString;
			
			// define the file to be read and create variable to hold line being read
			StreamReader textfile = new StreamReader(filepath);
			
            // import the first row to be used as index later
            List<string> list_indexer = Regex.Split(textfile.ReadLine(),deliminator).ToList();

            // get column count
            int columncount = list_indexer.Count;
			
            // create variables to hold imported values
            List<List<double>> listlist_values = new List<List<double>>();
            List<double> dummy_values = new List<double>(0);
			for (int i = 0; i < columncount; i++)
            {
                listlist_values.Add(dummy_values.ToList());
            }
			
			// loop and import all rows of data
            string line;
            string[] splittedline;
            List<double> line_values = new List<double>();
			double value;
            while((line = textfile.ReadLine()) != null)
            {
                // split data with deliminator
                splittedline = Regex.Split(line,deliminator);
                
				// convert to double
                for (int i = 0; i < splittedline.Length; i++)
                {
                    double.TryParse(splittedline[i], NumberStyles.Any, CultureInfo.CurrentCulture, out value);
					line_values.Add(value);
                }

                // check column count: if line_values has more column than columncount then extend list_indexer and listlist_values, other wise extend line_values
                if (line_values.Count > columncount)
                {
                    for (int i = 0; i < (line_values.Count - columncount); i++)
                    {
                        list_indexer.Add("");
                        listlist_values.Add(dummy_values.ToList());
                    }
					columncount = line_values.Count;
                }
                else if (line_values.Count < columncount)
                {
                    for (int i = 0; i < (columncount - line_values.Count); i++)
                    {
                        line_values.Add(Constants.BlankValue);
                    }
                }

                // add to listlist_values
                for (int i = 0; i < columncount; i++)
                {
					listlist_values[i].Add(line_values[i]);
                }
                
				// clear line_values
				line_values.Clear();
				
                // increment size of dummy_values
                dummy_values.Add(Constants.BlankValue);
            }

            // close text file
            textfile.Close();

            // convert to Tyche variables
            var indexer = parameters.VariableFactory.Indexer.Create(list_indexer.ToArray());
            List<IVariable> outputSubVariables = new List<IVariable>();
            for (int i = 0; i < columncount; i++)
            {
                outputSubVariables.Add(parameters.VariableFactory.Scalar.Create(DataType.Double, parameters.StochasticValueFactory.Create(listlist_values[i].ToArray())));
            }
                       
            var outputVariable = parameters.VariableFactory.Matrix.Wrap(DimensionName, outputSubVariables, indexer);

            // output results
            parameters.OutputVariables.Add(outputVariable);
            return true;
        }
    }
}
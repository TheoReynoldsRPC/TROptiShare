using System;
using Tyche.API.CSharp;

namespace Char
{
	/// <summary>
	/// This file contains the ImportCSVPivot function it allows the
	/// import of CSV or Tab deliminated text files
	/// Where there are thousands of columns
	/// then it pivots the data into a three column stochastic string vector output (RowID, ColumnID, CellValue )
	/// </summary>
	/// <remarks>
	/// In Tyche this function is coded as like this
	/// Output_Variable = ImportCSVPivot(Import_File, Delimitator_Char);
	/// </remarks>

	/// <summary>
	/// This is an abstract class that is capable of facilitating all the TableGroup Operations
	/// </summary>
	public class Char : ICSharpOperation
	{
		public ICSharpOperationMetadata Metadata { get; } = new CSharpOperationMetadata
		{
			MinInputs = 1,
			MaxInputs = 1,
			NumOutputs = 1,
			IsPure = true
		};

		/// <summary>
		/// Determine the output types, given the input types.
		/// </summary>
		public virtual bool Resolve(IResolveParameters parameters)
		{
			if(!parameters.InputTypeData.ValidateAtIndex(0, TypeIs.AnInteger & ShapeIs.AScalar))
			{
				return false;
			}

			parameters.OutputTypeData.Add(parameters.TypeDetailsFactory.Create(DataType.String));
			return true;
		}

		/// <summary>
		/// The main function execute procedure.
		/// </summary>
		public virtual bool Execute(IExecuteParameters parameters)
		{
			if(!parameters.InputVariables.ValidateAtIndex(0, VariableIs.Deterministic))
			{
				return false;
			}

			var char_IndexVariable = parameters.InputVariables[0];
			char? output_Char;

			if(char_IndexVariable.DeterministicValueAsInteger == Constants.BlankValue)
			{
				output_Char = '\t';
			}
			else
			{
				output_Char = Convert.ToChar(char_IndexVariable.DeterministicValueAsInteger);
			}

			var outputVariable = parameters.VariableFactory.Scalar.Create(!output_Char.HasValue ? TycheSupport.Primitives.TycheConstants.BlankString : output_Char.ToString());


			parameters.OutputVariables.Add(outputVariable);
			return true;
		}
	}
}
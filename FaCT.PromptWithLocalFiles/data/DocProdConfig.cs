namespace MoE.Commercial.Documents.Generation.DocProd.Models
{
	public class DocProdConfig
	{
		public CredentialsConfig Credentials { get; set; }
		public int PollingInterval { get; set; }
		public string ExtractFileSourceFolderPath { get; set; }
		public string ExtractFileDestinationFolderPath { get; set; }
		public string ExtractFileName { get; set; }
        public List<string> BackUpFiles { get; set; }
		public List<string> PrintFiles { get; set; }
		public string PrintFolder { get; set; }
		public string BackUpFolder { get; set; }
		public string TableSourceFolder { get; set; }
		public string TableDestinationFolder { get; set; }
		public Programs Programs { get; set; }
		public string RootFolder { get; set; }
		public List<string> DeleteFolders { get; set; }
		public List<string> MidRunCleanup { get; set; }
		public string CommercialDocProdApiRoute { get; set; }
		public string BlobStorageContainerName { get; set; }
		public string WorkFolder { get; set; }
        public List<string> WorkSubFolders { get; set; }
    }

	public class CredentialsConfig
	{
		public string User { get; set; }
		public string Password { get; set; }
	}

	public class Programs
	{
		public ProgramConfig GeneratePDF { get; set; }
        public ProgramConfig GenerateHTML { get; set; }
        public ProgramConfig GenerateTransactionFile { get; set; }
		public ProgramConfig GenerateDataModule { get; set; }
		public ProgramConfig ConversionBatchFile { get; set; }
		public ProgramConfig GenerateWip { get; set; }
		public ProgramConfig PrintProgram { get; set; }
		public ProgramConfig ArchivePrintData { get; set; }
		public ProgramConfig Program7 { get; set; }
		public ProgramConfig GeneratePDFStage1 { get; set; }
		public ProgramConfig GeneratePDFStage2 { get; set; }
		public ProgramConfig GeneratePDFStage3 { get; set; }
		public ProgramConfig GeneratePDFStage4 { get; set; }
	}

	public class ProgramConfig : IProgramConfig
	{
		public string Path { get; set; }
		public List<string> Arguments { get; set; }
		public string WorkingFolder { get; set; }
		public int RunOrder { get; set; }
	}

	public interface IProgramConfig
	{
		string Path { get; set; }
		List<string> Arguments { get; set; }
		string WorkingFolder { get; set; }
		int RunOrder { get; set; }
	}
}

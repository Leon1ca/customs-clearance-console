namespace CustomsClearanceConsole;

internal sealed partial class BatchScanner
{
    public BatchScanner() : this(new DocumentExtractor().ExtractAsync) { }
}

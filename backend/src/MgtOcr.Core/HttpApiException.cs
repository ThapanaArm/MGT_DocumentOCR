namespace MgtOcr.Core;

// Mirrors FastAPI's HTTPException — thrown from anywhere (controllers or shared repository/engine
// code, matching Python's get_document()/etc. raising HTTPException deep inside helper functions)
// and turned into {"detail": ...} with the given status by middleware in Program.cs.
public class HttpApiException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Detail { get; } = detail;
}

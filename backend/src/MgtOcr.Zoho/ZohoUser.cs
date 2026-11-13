namespace MgtOcr.Zoho;

/// <summary>An active, confirmed Zoho CRM user -- a Sales person a record (e.g. a new Deal) can be
/// assigned to as its Owner. From GET /crm/v8/users?type=ActiveConfirmedUsers (ZohoClient.GetActiveUsersAsync).</summary>
public record ZohoUser(string Id, string Name, string? Email, string? Role, string? Profile);

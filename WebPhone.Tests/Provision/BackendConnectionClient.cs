using WebPhone.Services;

namespace WebPhone.Tests.Provision;

public class BackendConnectionClient(HttpClient httpClient, string baseUrl, string userId)
    : BackendClient(baseUrl, new TestProfile(userId), httpClient: httpClient) { }

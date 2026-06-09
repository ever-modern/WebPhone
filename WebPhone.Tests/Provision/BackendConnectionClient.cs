using WebPhone.Services;

namespace WebPhone.Tests.Provision;

public class BackendConnectionClient(string baseUrl, string userId)
    : BackendClient(baseUrl, new TestProfile(userId)) { }

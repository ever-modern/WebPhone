namespace WebPhone.Backend;

public class UserFaultException : Exception
{
    public UserFaultException(string message) : base(message)
    {
    }
}
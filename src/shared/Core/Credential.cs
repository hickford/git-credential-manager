
namespace GitCredentialManager
{
    /// <summary>
    /// Represents a credential.
    /// </summary>
    public interface ICredential
    {
        /// <summary>
        /// Account associated with this credential.
        /// </summary>
        string Account { get; }

        /// <summary>
        /// Password.
        /// </summary>
        string Password { get; }

        string PasswordExpiryUTC { get => null; }
        
        string OAuthRefreshToken { get => null; }
    }

    /// <summary>
    /// Represents a credential (username/password pair) that Git can use to authenticate to a remote repository.
    /// </summary>
    public record GitCredential : ICredential
    {
        public GitCredential(string userName, string password)
        {
            Account = userName;
            Password = password;
        }

        public GitCredential(InputArguments input)
        {
            Account = input.UserName;
            Password = input.Password;
            PasswordExpiryUTC = input.PasswordExpiryUTC;
            OAuthRefreshToken = input.OAuthRefreshToken;
        }

        public string Account { get; init; }

        public string Password { get; init; }

        public string PasswordExpiryUTC { get; init; }
        
        public string OAuthRefreshToken { get; init; }
    }
}

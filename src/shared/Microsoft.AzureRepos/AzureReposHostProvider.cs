using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GitCredentialManager;
using GitCredentialManager.Authentication;
using GitCredentialManager.Commands;
using KnownGitCfg = GitCredentialManager.Constants.GitConfiguration;

namespace Microsoft.AzureRepos
{
    public class AzureReposHostProvider : DisposableObject, IHostProvider, IConfigurableComponent, ICommandProvider
    {
        private readonly ICommandContext _context;
        private readonly IAzureDevOpsRestApi _azDevOps;
        private readonly IMicrosoftAuthentication _msAuth;
        private readonly IAzureDevOpsAuthorityCache _authorityCache;
        private readonly IAzureReposBindingManager _bindingManager;

        public AzureReposHostProvider(ICommandContext context)
            : this(context, new AzureDevOpsRestApi(context), new MicrosoftAuthentication(context),
                new AzureDevOpsAuthorityCache(context), new AzureReposBindingManager(context))
        {
        }

        public AzureReposHostProvider(ICommandContext context, IAzureDevOpsRestApi azDevOps,
            IMicrosoftAuthentication msAuth, IAzureDevOpsAuthorityCache authorityCache,
            IAzureReposBindingManager bindingManager)
        {
            EnsureArgument.NotNull(context, nameof(context));
            EnsureArgument.NotNull(azDevOps, nameof(azDevOps));
            EnsureArgument.NotNull(msAuth, nameof(msAuth));
            EnsureArgument.NotNull(authorityCache, nameof(authorityCache));
            EnsureArgument.NotNull(bindingManager, nameof(bindingManager));

            _context = context;
            _azDevOps = azDevOps;
            _msAuth = msAuth;
            _authorityCache = authorityCache;
            _bindingManager = bindingManager;
        }

        #region IHostProvider

        public string Id => "azure-repos";

        public string Name => "Azure Repos";

        public IEnumerable<string> SupportedAuthorityIds => MicrosoftAuthentication.AuthorityIds;

        public bool IsSupported(InputArguments input)
        {
            if (input is null)
            {
                return false;
            }

            // We do not support unencrypted HTTP communications to Azure Repos,
            // but we report `true` here for HTTP so that we can show a helpful
            // error message for the user in `CreateCredentialAsync`.
            return input.TryGetHostAndPort(out string hostName, out _)
                   && (StringComparer.OrdinalIgnoreCase.Equals(input.Protocol, "http") ||
                       StringComparer.OrdinalIgnoreCase.Equals(input.Protocol, "https")) &&
                   UriHelpers.IsAzureDevOpsHost(hostName);
        }

        public bool IsSupported(HttpResponseMessage response)
        {
            // Azure DevOps Server (TFS) is handled by the generic provider, which supports basic auth, and WIA detection.
            return false;
        }

        public async Task<ICredential> GetCredentialAsync(InputArguments input)
        {
                Uri remoteUri = input.GetRemoteUri();
                string service = GetServiceName(remoteUri);
                string account = GetAccountNameForCredentialQuery(input);

                _context.Trace.WriteLine($"Looking for existing credential in store with service={service} account={account}...");

                ICredential credential = _context.CredentialStore.Get(service, account);
                if (credential == null)
                {
                    _context.Trace.WriteLine("No existing credentials found.");

                    // No existing credential was found, create a new one
                    _context.Trace.WriteLine("Creating new credential...");
                    credential = await GeneratePersonalAccessTokenAsync(input);
                    _context.Trace.WriteLine("Credential created.");
                }
                else
                {
                    _context.Trace.WriteLine("Existing credential found.");
                }

                return credential;
        }

        public Task StoreCredentialAsync(InputArguments input)
        {
            Uri remoteUri = input.GetRemoteUri();

                string service = GetServiceName(remoteUri);

                // We always store credentials against the given username argument for
                // both vs.com and dev.azure.com-style URLs.
                string account = input.UserName;

                // Add or update the credential in the store.
                _context.Trace.WriteLine($"Storing credential with service={service} account={account}...");
                _context.CredentialStore.AddOrUpdate(service, account, input.Password);
                _context.Trace.WriteLine("Credential was successfully stored.");
            return Task.CompletedTask;
        }

        public Task EraseCredentialAsync(InputArguments input)
        {
            Uri remoteUri = input.GetRemoteUri();

                string service = GetServiceName(remoteUri);
                string account = GetAccountNameForCredentialQuery(input);

                // Try to locate an existing credential
                _context.Trace.WriteLine($"Erasing stored credential in store with service={service} account={account}...");
                if (_context.CredentialStore.Remove(service, account))
                {
                    _context.Trace.WriteLine("Credential was successfully erased.");
                }
                else
                {
                    _context.Trace.WriteLine("No credential was erased.");
                }

            return Task.CompletedTask;
        }

        protected override void ReleaseManagedResources()
        {
            _azDevOps.Dispose();
            base.ReleaseManagedResources();
        }

        private async Task<ICredential> GeneratePersonalAccessTokenAsync(InputArguments input)
        {
            ThrowIfDisposed();

            // We should not allow unencrypted communication and should inform the user
            if (StringComparer.OrdinalIgnoreCase.Equals(input.Protocol, "http"))
            {
                throw new Trace2Exception(_context.Trace2,
                    "Unencrypted HTTP is not supported for Azure Repos. Ensure the repository remote URL is using HTTPS.");
            }

            Uri remoteUri = input.GetRemoteUri();
            Uri orgUri = UriHelpers.CreateOrganizationUri(remoteUri, out _);

            // Determine the MS authentication authority for this organization
            _context.Trace.WriteLine("Determining Microsoft Authentication Authority...");
            string authAuthority = await _azDevOps.GetAuthorityAsync(orgUri);
            _context.Trace.WriteLine($"Authority is '{authAuthority}'.");

            // Get an AAD access token for the Azure DevOps SPS
            _context.Trace.WriteLine("Getting Azure AD access token...");
            IMicrosoftAuthenticationResult result = await _msAuth.GetTokenAsync(
                authAuthority,
                GetClientId(),
                GetRedirectUri(),
                AzureDevOpsConstants.AzureDevOpsDefaultScopes,
                null);
            _context.Trace.WriteLineSecrets(
                $"Acquired Azure access token. Account='{result.AccountUpn}' Token='{{0}}'", new object[] {result.AccessToken});

            // Ask the Azure DevOps instance to create a new PAT
            var patScopes = new[]
            {
                AzureDevOpsConstants.PersonalAccessTokenScopes.ReposWrite,
                AzureDevOpsConstants.PersonalAccessTokenScopes.ArtifactsRead
            };
            _context.Trace.WriteLine($"Creating Azure DevOps PAT with scopes '{string.Join(", ", patScopes)}'...");
            string pat = await _azDevOps.CreatePersonalAccessTokenAsync(
                orgUri,
                result.AccessToken,
                patScopes);
            _context.Trace.WriteLineSecrets("PAT created. PAT='{0}'", new object[] {pat});

            return new GitCredential(result.AccountUpn, pat);
        }

        internal /* for testing purposes */ static bool TryGetAuthorityFromHeaders(IEnumerable<string> headers, out string authority)
        {
            authority = null;

            if (headers is null)
            {
                return false;
            }

            var regex = new Regex(@"authorization_uri=""?(?<authority>.+)""?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (string header in headers)
            {
                Match match = regex.Match(header);
                if (match.Success)
                {
                    authority = match.Groups["authority"].Value.Trim(new[] { '"', '\'' });
                    return true;
                }
            }

            return false;
        }

        private string GetClientId()
        {
            // Check for developer override value
            if (_context.Settings.TryGetSetting(
                    AzureDevOpsConstants.EnvironmentVariables.DevAadClientId,
                    Constants.GitConfiguration.Credential.SectionName,
                    AzureDevOpsConstants.GitConfiguration.Credential.DevAadClientId,
                    out string clientId))
            {
                return clientId;
            }

            return AzureDevOpsConstants.AadClientId;
        }

        private Uri GetRedirectUri()
        {
            // Check for developer override value
            if (_context.Settings.TryGetSetting(
                    AzureDevOpsConstants.EnvironmentVariables.DevAadRedirectUri,
                    Constants.GitConfiguration.Credential.SectionName, AzureDevOpsConstants.GitConfiguration.Credential.DevAadRedirectUri,
                    out string redirectUriStr) &&
                Uri.TryCreate(redirectUriStr, UriKind.Absolute, out Uri redirectUri))
            {
                return redirectUri;
            }

            return AzureDevOpsConstants.AadRedirectUri;
        }

        /// <remarks>
        /// For dev.azure.com-style URLs we use the path arg to get the Azure DevOps organization name.
        /// We ensure the presence of the path arg by setting credential.useHttpPath = true at install time.
        ///
        /// The result of this workaround is that we are now unable to determine if the user wanted to store
        /// credentials with the full path or not for dev.azure.com-style URLs.
        ///
        /// Rather than always assume we're storing credentials against the full path, and therefore resulting
        /// in an personal access token being created per remote URL/repository, we never store against
        /// the full path and always store with the organization URL "dev.azure.com/org".
        ///
        /// For visualstudio.com-style URLs we know the AzDevOps organization name from the host arg, and
        /// don't set the useHttpPath option. This means if we get the full path for a vs.com-style URL
        /// we can store against the full remote path (the intended design).
        ///
        /// Users that need to clone a repository from Azure Repos against the full path therefore must
        /// use the vs.com-style remote URL and not the dev.azure.com one.
        /// </remarks>
        private static string GetServiceName(Uri remoteUri)
        {
            // dev.azure.com
            if (UriHelpers.IsDevAzureComHost(remoteUri.Host))
            {
                // We can never store the new dev.azure.com-style URLs against the full path because
                // we have forced the useHttpPath option to true to in order to retrieve the AzDevOps
                // organization name from Git.
                return UriHelpers.CreateOrganizationUri(remoteUri, out _).AbsoluteUri.TrimEnd('/');
            }

            // *.visualstudio.com
            if (UriHelpers.IsVisualStudioComHost(remoteUri.Host))
            {
                // If we're given the full path for an older *.visualstudio.com-style URL then we should
                // respect that in the service name.
                return remoteUri.AbsoluteUri.TrimEnd('/');
            }

            throw new InvalidOperationException("Host is not Azure DevOps.");
        }

        private static string GetAccountNameForCredentialQuery(InputArguments input)
        {
            if (!input.TryGetHostAndPort(out string hostName, out _))
            {
                throw new InvalidOperationException("Failed to parse host name and/or port");
            }

            // dev.azure.com
            if (UriHelpers.IsDevAzureComHost(hostName))
            {
                // We ignore the given username for dev.azure.com-style URLs because AzDevOps recommends
                // adding the organization name as the user in the remote URL (resulting in URLs like
                // https://org@dev.azure.com/org/foo/_git/bar) and we don't know if the given username
                // is an actual username, or the org name.
                // Use `null` as the account name so we match all possible credentials (regardless of
                // the account).
                return null;
            }

            // *.visualstudio.com
            if (UriHelpers.IsVisualStudioComHost(hostName))
            {
                // If we're given a username for the vs.com-style URLs we can and should respect any
                // specified username in the remote URL/input arguments.
                return input.UserName;
            }

            throw new InvalidOperationException("Host is not Azure DevOps.");
        }


        #endregion

        #region IConfigurationComponent

        string IConfigurableComponent.Name => "Azure Repos provider";

        public Task ConfigureAsync(ConfigurationTarget target)
        {
            string useHttpPathKey = $"{KnownGitCfg.Credential.SectionName}.https://dev.azure.com.{KnownGitCfg.Credential.UseHttpPath}";

            GitConfigurationLevel configurationLevel = target == ConfigurationTarget.System
                ? GitConfigurationLevel.System
                : GitConfigurationLevel.Global;

            IGitConfiguration targetConfig = _context.Git.GetConfiguration();

            if (targetConfig.TryGet(useHttpPathKey, false, out string currentValue) && currentValue.IsTruthy())
            {
                _context.Trace.WriteLine("Git configuration 'credential.useHttpPath' is already set to 'true' for https://dev.azure.com.");
            }
            else
            {
                _context.Trace.WriteLine("Setting Git configuration 'credential.useHttpPath' to 'true' for https://dev.azure.com...");
                targetConfig.Set(configurationLevel, useHttpPathKey, "true");
            }

            return Task.CompletedTask;
        }

        public Task UnconfigureAsync(ConfigurationTarget target)
        {
            string helperKey = $"{Constants.GitConfiguration.Credential.SectionName}.{Constants.GitConfiguration.Credential.Helper}";
            string useHttpPathKey = $"{KnownGitCfg.Credential.SectionName}.https://dev.azure.com.{KnownGitCfg.Credential.UseHttpPath}";

            _context.Trace.WriteLine("Clearing Git configuration 'credential.useHttpPath' for https://dev.azure.com...");

            GitConfigurationLevel configurationLevel = target == ConfigurationTarget.System
                ? GitConfigurationLevel.System
                : GitConfigurationLevel.Global;

            IGitConfiguration targetConfig = _context.Git.GetConfiguration();

            // On Windows, if there is a "manager" or "manager-core" entry remaining in the system config then we must
            // not clear the useHttpPath option otherwise this would break the bundled version of GCM in Git for Windows.
            if (!PlatformUtils.IsWindows() || target != ConfigurationTarget.System ||
                targetConfig.GetAll(helperKey).All(x => !string.Equals(x, "manager") && !string.Equals(x, "manager-core")))
            {
                targetConfig.Unset(configurationLevel, useHttpPathKey);
            }

            return Task.CompletedTask;
        }

        #endregion

        #region ICommandProvider

        ProviderCommand ICommandProvider.CreateCommand()
        {
            //
            // clear-cache
            //
            var clearCacheCmd = new Command("clear-cache", "Clear the Azure authority cache");
            clearCacheCmd.SetHandler(ClearCacheCmd);

            //
            // list <organization> [--show-remotes] [--verbose]
            //
            var listCmd = new Command("list", "List all user account bindings");
            var orgFilterArg = new Argument<string>("organization", "(optional) Filter results by Azure DevOps organization name")
            {
                Arity = ArgumentArity.ZeroOrOne
            };
            var remoteOpt = new Option<bool>("--show-remotes")
            {
                Description = "Also show Azure DevOps remote user bindings for the current repository"
            };
            var verboseOpt = new Option<bool>(new[] { "--verbose", "-v" }, "Verbose output - show remote URLs");
            listCmd.AddArgument(orgFilterArg);
            listCmd.AddOption(remoteOpt);
            listCmd.AddOption(verboseOpt);
            listCmd.SetHandler(ListCmd, orgFilterArg, remoteOpt, verboseOpt);

            //
            // bind <organization> <username> [--local]
            //
            var bindCmd = new Command("bind", "Bind a user account to an Azure DevOps organization");
            var orgArg = new Argument<string>("organization", "Azure DevOps organization name")
            {
                Arity = ArgumentArity.ExactlyOne
            };
            var userNameArg = new Argument<string>("username", "Username or email (e.g.: alice@example.com)")
            {
                Arity = ArgumentArity.ExactlyOne
            };
            var localOpt = new Option<bool>("--local", "Target the local repository Git configuration");
            bindCmd.AddArgument(orgArg);
            bindCmd.AddArgument(userNameArg);
            bindCmd.AddOption(localOpt);
            bindCmd.SetHandler(BindCmd, orgArg, userNameArg, localOpt);

            //
            // unbind <organization> [--local]
            //
            var unbindCmd = new Command("unbind")
            {
                Description = "Remove user account binding for an Azure DevOps organization",
            };
            unbindCmd.AddArgument(orgArg);
            unbindCmd.AddOption(localOpt);
            unbindCmd.SetHandler(UnbindCmd, orgArg, localOpt);

            var rootCmd = new ProviderCommand(this);
            rootCmd.AddCommand(listCmd);
            rootCmd.AddCommand(bindCmd);
            rootCmd.AddCommand(unbindCmd);
            rootCmd.AddCommand(clearCacheCmd);
            return rootCmd;
        }

        private void ClearCacheCmd()
        {
            _authorityCache.Clear();
            _context.Streams.Out.WriteLine("Authority cache cleared");
        }

        private class RemoteBinding
        {
            public string Remote { get; set; }
            public bool IsPush { get; set; }
            public Uri Uri { get; set; }
        }

        private void ListCmd(string organization, bool showRemotes, bool verbose)
        {
            // Get all organization bindings from the user manager
            IList<AzureReposBinding> bindings = _bindingManager.GetBindings(organization).ToList();
            IDictionary<string, IEnumerable<AzureReposBinding>> orgBindingMap =
                bindings.GroupBy(x => x.Organization).ToDictionary();

            // If we are asked to also show remotes we build the remote binding map
            var orgRemotesMap = new Dictionary<string, ICollection<RemoteBinding>>();
            if (showRemotes)
            {
                if (!_context.Git.IsInsideRepository())
                {
                    _context.Streams.Error.WriteLine("warning: not inside a git repository (--show-remotes has no effect)");
                }

                static bool IsAzureDevOpsHttpRemote(string url, out Uri uri)
                {
                    return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                           (StringComparer.OrdinalIgnoreCase.Equals(Uri.UriSchemeHttp, uri.Scheme) ||
                            StringComparer.OrdinalIgnoreCase.Equals(Uri.UriSchemeHttps, uri.Scheme)) &&
                           UriHelpers.IsAzureDevOpsHost(uri.Host);
                }

                foreach (GitRemote remote in _context.Git.GetRemotes())
                {
                    if (IsAzureDevOpsHttpRemote(remote.FetchUrl, out Uri fetchUri))
                    {
                        string fetchOrg = UriHelpers.GetOrganizationName(fetchUri);
                        var binding = new RemoteBinding {IsPush = false, Remote = remote.Name, Uri = fetchUri};
                        orgRemotesMap.Append(fetchOrg, binding);
                    }

                    if (IsAzureDevOpsHttpRemote(remote.PushUrl, out Uri pushUri))
                    {
                        string pushOrg = UriHelpers.GetOrganizationName(pushUri);
                        var binding = new RemoteBinding {IsPush = true, Remote = remote.Name, Uri = pushUri};
                        orgRemotesMap.Append(pushOrg, binding);
                    }
                }
            }

            bool isFiltered = !string.IsNullOrWhiteSpace(organization);
            string indent = isFiltered ? string.Empty : "  ";

            // Get the set of all organization names (organization names are not case sensitive)
            ISet<string> orgNames = new HashSet<string>(orgBindingMap.Keys, StringComparer.OrdinalIgnoreCase);
            orgNames.UnionWith(orgRemotesMap.Keys);

            var icmp = StringComparer.OrdinalIgnoreCase;

            foreach (string orgName in orgNames)
            {
                if (!isFiltered)
                {
                    _context.Streams.Out.WriteLine($"{orgName}:");
                }

                // Print organization bindings
                foreach (AzureReposBinding binding in orgBindingMap.GetValues(orgName))
                {
                    if (binding.GlobalUserName != null)
                    {
                        _context.Streams.Out.WriteLine($"{indent}(global) -> {binding.GlobalUserName}");
                    }

                    if (binding.LocalUserName != null)
                    {
                        string value = string.IsNullOrEmpty(binding.LocalUserName)
                            ? "(no inherit)"
                            : binding.LocalUserName;
                        _context.Streams.Out.WriteLine($"{indent}(local)  -> {value}");
                    }
                }

                // Print remote bindings
                IEnumerable<IGrouping<string, RemoteBinding>> remoteBindingMap =
                    orgRemotesMap.GetValues(orgName).GroupBy(x => x.Remote);

                foreach (var remoteBinding in remoteBindingMap)
                {
                    _context.Streams.Out.WriteLine($"{indent}{remoteBinding.Key}:");
                    foreach (RemoteBinding binding in remoteBinding)
                    {
                        // User names in dev.azure.com URLs cannot always be used as *actual user names*
                        // because of the unfortunate decision to use this field to get the Azure DevOps
                        // organization name to be sent by Git to credential helpers.
                        //
                        // We show dev.azure.com URLs as "inherit", if there is a username that matches
                        // the organization name.
                        if (!binding.Uri.TryGetUserInfo(out string userName, out _) ||
                            UriHelpers.IsDevAzureComHost(binding.Uri.Host) && icmp.Equals(userName, orgName))
                        {
                            userName = "(inherit)";
                        }

                        string url = null;
                        if (verbose)
                        {
                            url = $"{binding.Uri.WithoutUserInfo()} ";
                        }

                        _context.Streams.Out.WriteLine(binding.IsPush
                            ? $"{indent}  {url}(push)  -> {userName}"
                            : $"{indent}  {url}(fetch) -> {userName}");
                    }
                }
            }
        }

        private Task<int> BindCmd(string organization, string userName, bool local)
        {
            if (local && !_context.Git.IsInsideRepository())
            {
                _context.Streams.Error.WriteLine("error: not inside a git repository (cannot use --local)");
                return Task.FromResult(-1);
            }

            _bindingManager.Bind(organization, userName, local);
            return Task.FromResult(0);
        }

        private Task<int> UnbindCmd(string organization, bool local)
        {
            if (local && !_context.Git.IsInsideRepository())
            {
                _context.Streams.Error.WriteLine("error: not inside a git repository (cannot use --local)");
                return Task.FromResult(-1);
            }

            _bindingManager.Unbind(organization, local);
            return Task.FromResult(0);
        }

        #endregion
    }
}

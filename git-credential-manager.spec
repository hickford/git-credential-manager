%global srcname git-credential-manager

Name: git-credential-manager
Version: 2.0.785
Release: 2%{?dist}
License: MIT
Summary: A secure Git credential helper with multi-factor authentication support
Url: https://github.com/GitCredentialManager/git-credential-manager
Source0: %{name}-%{version}.tar.gz

BuildRequires: dotnet-sdk-6.0
Requires: dotnet-runtime-6.0

%description
Git Credential Manager is a secure Git credential helper. GCM provides multi-factor authentication support for Azure DevOps, Azure DevOps Server, GitHub, Bitbucket, and GitLab.

%global debug_package %{nil}

#-- PREP, BUILD & INSTALL -----------------------------------------------------#
%prep
%autosetup

%build
dotnet publish src/shared/Git-Credential-Manager/ --configuration=Release --framework=net6.0

%install
mkdir -p %{buildroot}%{_datadir}/gcm-core
cp -r out/shared/Git-Credential-Manager/bin/Release/net6.0/publish/. %{buildroot}%{_datadir}/gcm-core
mkdir -p %{buildroot}%{_bindir}
ln -s ../share/gcm-core/git-credential-manager-core %{buildroot}%{_bindir}/git-credential-manager-core 

#-- FILES ---------------------------------------------------------------------#
%files
%dir %{_datadir}/gcm-core
%{_datadir}/gcm-core/Atlassian.Bitbucket.dll
%{_datadir}/gcm-core/Atlassian.Bitbucket.pdb
%{_datadir}/gcm-core/GitHub.dll
%{_datadir}/gcm-core/GitHub.pdb
%{_datadir}/gcm-core/GitLab.dll
%{_datadir}/gcm-core/GitLab.pdb
%{_datadir}/gcm-core/Microsoft.AzureRepos.dll
%{_datadir}/gcm-core/Microsoft.AzureRepos.pdb
%{_datadir}/gcm-core/Microsoft.Identity.Client.Extensions.Msal.dll
%{_datadir}/gcm-core/Microsoft.Identity.Client.dll
%{_datadir}/gcm-core/NOTICE
%{_datadir}/gcm-core/Newtonsoft.Json.dll
%{_datadir}/gcm-core/System.CommandLine.dll
%{_datadir}/gcm-core/System.Security.Cryptography.ProtectedData.dll
%{_datadir}/gcm-core/cs/System.CommandLine.resources.dll
%{_datadir}/gcm-core/de/System.CommandLine.resources.dll
%{_datadir}/gcm-core/es/System.CommandLine.resources.dll
%{_datadir}/gcm-core/fr/System.CommandLine.resources.dll
%{_datadir}/gcm-core/gcmcore.dll
%{_datadir}/gcm-core/gcmcore.pdb
%{_datadir}/gcm-core/git-credential-manager-core
%{_datadir}/gcm-core/git-credential-manager-core.deps.json
%{_datadir}/gcm-core/git-credential-manager-core.dll
%{_datadir}/gcm-core/git-credential-manager-core.pdb
%{_datadir}/gcm-core/git-credential-manager-core.runtimeconfig.json
%{_datadir}/gcm-core/it/System.CommandLine.resources.dll
%{_datadir}/gcm-core/ja/System.CommandLine.resources.dll
%{_datadir}/gcm-core/ko/System.CommandLine.resources.dll
%{_datadir}/gcm-core/pl/System.CommandLine.resources.dll
%{_datadir}/gcm-core/pt-BR/System.CommandLine.resources.dll
%{_datadir}/gcm-core/ru/System.CommandLine.resources.dll
%{_datadir}/gcm-core/runtimes/win/lib/netstandard2.0/System.Security.Cryptography.ProtectedData.dll
%{_datadir}/gcm-core/tr/System.CommandLine.resources.dll
%{_datadir}/gcm-core/zh-Hans/System.CommandLine.resources.dll
%{_datadir}/gcm-core/zh-Hant/System.CommandLine.resources.dll
%{_bindir}/git-credential-manager-core

#-- CHANGELOG -----------------------------------------------------------------#
%changelog
* Tue Aug 02 2022 M Hickford <mirth.hickford@gmail.com> 2.0.785-2
- rpm spec (mirth.hickford@gmail.com)

* Sun Jul 31 2022 M Hickford <mirth.hickford@gmail.com>
-

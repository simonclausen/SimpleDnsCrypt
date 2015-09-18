﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using Caliburn.Micro;
using SimpleDnsCrypt.Config;
using SimpleDnsCrypt.Models;
using SimpleDnsCrypt.Tools;
using WPFLocalizeExtension.Engine;

namespace SimpleDnsCrypt.ViewModels
{
    /// <summary>
    ///     The MainViewModel.
    /// </summary>
    [Export]
    public sealed class MainViewModel : Screen, IShell
    {
        private readonly BindableCollection<LocalNetworkInterface> _localNetworkInterfaces =
            new BindableCollection<LocalNetworkInterface>();

        private readonly IWindowManager _windowManager;
        private bool _actAsGlobalGateway;
        private bool _isDebugModeEnabled;
        private bool _isPrimaryResolverRunning;
        private bool _isRefreshingResolverList;
        private bool _isSecondaryResolverRunning;
        private bool _isUninstallingServices;
        private bool _isWorkingOnPrimaryService;
        private bool _isWorkingOnSecondaryService;
        private int _overlayDependencies;
        private List<string> _plugins;


        private DnsCryptProxyEntry _primaryResolver;
        private string _primaryResolverTitle;
        private List<DnsCryptProxyEntry> _resolvers;
        private DnsCryptProxyEntry _secondaryResolver;
        private string _secondaryResolverTitle;

        private bool _showHiddenCards;
        private bool _useTcpOnly;

        /// <summary>
        ///     MainViewModel construcor for XAML.
        /// </summary>
        public MainViewModel()
        {
        }

        /// <summary>
        ///     MainViewModel construcor.
        /// </summary>
        /// <param name="windowManager">The current window manager.</param>
        /// <param name="eventAggregator">The event aggregator.</param>
        /// <param name="dnsCrypt"></param>
        [ImportingConstructor]
        private MainViewModel(IWindowManager windowManager, IEventAggregator eventAggregator)
        {
            _windowManager = windowManager;
            eventAggregator.Subscribe(this);


            LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
            LocalizeDictionary.Instance.Culture = Thread.CurrentThread.CurrentCulture;

            if (!IsAdministrator())
            {
                _windowManager.ShowMetroMessageBox(
                    LocalizationEx.GetUiString("dialog_message_bad_privileges", Thread.CurrentThread.CurrentCulture),
                    LocalizationEx.GetUiString("dialog_error_title", Thread.CurrentThread.CurrentCulture),
                    MessageBoxButton.OK, BoxType.Error);
                Environment.Exit(1);
            }

            if (!ValidateDnsCryptProxyFolder())
            {
                _windowManager.ShowMetroMessageBox(
                    LocalizationEx.GetUiString("dialog_message_missing_proxy_files", Thread.CurrentThread.CurrentCulture),
                    LocalizationEx.GetUiString("dialog_error_title", Thread.CurrentThread.CurrentCulture),
                    MessageBoxButton.OK, BoxType.Error);
                Environment.Exit(1);
            }

            DisplayName = string.Format("{0} {1} ({2})", Global.ApplicationName, VersionUtilities.PublishVersion,
                LocalizationEx.GetUiString("global_ipv6_disabled", Thread.CurrentThread.CurrentCulture));
            _isDebugModeEnabled = false;
            _resolvers = new List<DnsCryptProxyEntry>();
            _isWorkingOnPrimaryService = false;
            _isWorkingOnSecondaryService = false;

            LocalNetworkInterfaces = new CollectionViewSource {Source = _localNetworkInterfaces};
            PrimaryDnsCryptProxyManager = new DnsCryptProxyManager(DnsCryptProxyType.Primary);
            SecondaryDnsCryptProxyManager = new DnsCryptProxyManager(DnsCryptProxyType.Secondary);
            ShowHiddenCards = false;


            if ((PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.TcpOnly) ||
                (SecondaryDnsCryptProxyManager.DnsCryptProxy.Parameter.TcpOnly))
            {
                _useTcpOnly = true;
            }

            // check the primary resolver for plugins
            if (PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.Plugins.Any())
            {
                _plugins = PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.Plugins.ToList();
            }
            else
            {
                if (SecondaryDnsCryptProxyManager.DnsCryptProxy.Parameter.Plugins.Any())
                {
                    _plugins = SecondaryDnsCryptProxyManager.DnsCryptProxy.Parameter.Plugins.ToList();
                }
                else
                {
                    // no stored plugins
                    _plugins = new List<string>();
                }
            }

            var proxyList = Path.Combine(Directory.GetCurrentDirectory(),
                Global.DnsCryptProxyFolder, Global.DnsCryptProxyResolverListName);
            var proxyListSignature = Path.Combine(Directory.GetCurrentDirectory(),
                Global.DnsCryptProxyFolder, Global.DnsCryptProxySignatureFileName);
            if (!File.Exists(proxyList))
            {
                // download and verify the proxy list if there is none.
                // it`s a really small file, so go on.
                DnsCryptProxyListManager.UpdateResolverListAsync();
            }

            var dnsProxyList =
                DnsCryptProxyListManager.ReadProxyList(proxyList, proxyListSignature, true);
            if (dnsProxyList != null && dnsProxyList.Any())
            {
                foreach (var dnsProxy in dnsProxyList)
                {
                    if (
                        dnsProxy.ProviderPublicKey.Equals(
                            PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.ProviderKey))
                    {
                        _primaryResolver = dnsProxy;
                    }
                    if (
                        dnsProxy.ProviderPublicKey.Equals(
                            SecondaryDnsCryptProxyManager.DnsCryptProxy.Parameter.ProviderKey))
                    {
                        _secondaryResolver = dnsProxy;
                    }
                    _resolvers.Add(dnsProxy);
                }
            }
            else
            {
                _windowManager.ShowMetroMessageBox(
                    string.Format(
                        LocalizationEx.GetUiString("dialog_message_missing_file", Thread.CurrentThread.CurrentCulture),
                        proxyList, proxyListSignature),
                    LocalizationEx.GetUiString("dialog_error_title", Thread.CurrentThread.CurrentCulture),
                    MessageBoxButton.OK, BoxType.Error);
                Environment.Exit(1);
            }

            // if there is no selected resolver, add a default resolver
            if (PrimaryResolver == null)
            {
                var tmpResolver = dnsProxyList.SingleOrDefault(d => d.Name.Equals(Global.PrimaryBackupResolverName));
                if (tmpResolver == null)
                {
                    tmpResolver = dnsProxyList.SingleOrDefault(d => d.Name.Equals(Global.SecondaryBackupResolverName));
                }
                PrimaryResolver = tmpResolver;
            }


            if (PrimaryDnsCryptProxyManager.IsDnsCryptProxyInstalled())
            {
                if (PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning())
                {
                    _isPrimaryResolverRunning = true;
                }
            }

            if (SecondaryDnsCryptProxyManager.IsDnsCryptProxyInstalled())
            {
                if (SecondaryDnsCryptProxyManager.IsDnsCryptProxyRunning())
                {
                    _isSecondaryResolverRunning = true;
                }
            }

            if (PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.LocalAddress.Contains(Global.GlobalGatewayAddress))
            {
                _actAsGlobalGateway = true;
                _primaryResolverTitle = string.Format("{0} ({1}:53)",
                    LocalizationEx.GetUiString("default_settings_primary_header", Thread.CurrentThread.CurrentCulture),
                    Global.GlobalGatewayAddress);
            }
            else
            {
                _actAsGlobalGateway = false;
                _primaryResolverTitle = string.Format("{0} ({1}:{2})",
                    LocalizationEx.GetUiString("default_settings_primary_header", Thread.CurrentThread.CurrentCulture),
                    Global.PrimaryResolverAddress,
                    Global.PrimaryResolverPort);
            }


            _secondaryResolverTitle = string.Format("{0} ({1}:{2})",
                LocalizationEx.GetUiString("default_settings_secondary_header", Thread.CurrentThread.CurrentCulture),
                Global.SecondaryResolverAddress,
                Global.SecondaryResolverPort);
        }

        /// <summary>
        ///     Overlay management for MetroMessageBoxViewModel.
        /// </summary>
        public bool IsOverlayVisible
        {
            get { return _overlayDependencies > 0; }
        }

        public DnsCryptProxyManager PrimaryDnsCryptProxyManager { get; set; }
        public DnsCryptProxyManager SecondaryDnsCryptProxyManager { get; set; }

        public CollectionViewSource LocalNetworkInterfaces { get; set; }

        public List<DnsCryptProxyEntry> Resolvers
        {
            get { return _resolvers; }
            set
            {
                if (value.Equals(_resolvers)) return;
                _resolvers = value;
                NotifyOfPropertyChange(() => Resolvers);
            }
        }

        public DnsCryptProxyEntry PrimaryResolver
        {
            get { return _primaryResolver; }
            set
            {
                if (value.Equals(_primaryResolver)) return;
                _primaryResolver = value;
                ReloadResolver(DnsCryptProxyType.Primary);
                NotifyOfPropertyChange(() => PrimaryResolver);
            }
        }

        public DnsCryptProxyEntry SecondaryResolver
        {
            get { return _secondaryResolver; }
            set
            {
                if (value.Equals(_secondaryResolver)) return;
                _secondaryResolver = value;
                ReloadResolver(DnsCryptProxyType.Secondary);
                NotifyOfPropertyChange(() => SecondaryResolver);
            }
        }

        public bool ShowHiddenCards
        {
            get { return _showHiddenCards; }
            set
            {
                _showHiddenCards = value;
                LoadNetworkCards();
                NotifyOfPropertyChange(() => ShowHiddenCards);
            }
        }

        public string PrimaryResolverTitle
        {
            get { return _primaryResolverTitle; }
            set
            {
                _primaryResolverTitle = value;
                NotifyOfPropertyChange(() => PrimaryResolverTitle);
            }
        }

        public string SecondaryResolverTitle
        {
            get { return _secondaryResolverTitle; }
            set
            {
                _secondaryResolverTitle = value;
                NotifyOfPropertyChange(() => SecondaryResolverTitle);
            }
        }

        public bool IsPrimaryResolverRunning
        {
            get { return _isPrimaryResolverRunning; }
            set
            {
                HandleService(DnsCryptProxyType.Primary);
                NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
            }
        }

        public bool IsSecondaryResolverRunning
        {
            get { return _isSecondaryResolverRunning; }
            set
            {
                HandleService(DnsCryptProxyType.Secondary);
                NotifyOfPropertyChange(() => IsSecondaryResolverRunning);
            }
        }

        public bool IsWorkingOnPrimaryService
        {
            get { return _isWorkingOnPrimaryService; }
            set
            {
                _isWorkingOnPrimaryService = value;
                NotifyOfPropertyChange(() => IsWorkingOnPrimaryService);
            }
        }

        public bool IsWorkingOnSecondaryService
        {
            get { return _isWorkingOnSecondaryService; }
            set
            {
                _isWorkingOnSecondaryService = value;
                NotifyOfPropertyChange(() => IsWorkingOnSecondaryService);
            }
        }

        /// <summary>
        ///     Overlay management for MetroMessageBoxViewModel.
        /// </summary>
        public void ShowOverlay()
        {
            _overlayDependencies++;
            NotifyOfPropertyChange(() => IsOverlayVisible);
        }

        /// <summary>
        ///     Overlay management for MetroMessageBoxViewModel.
        /// </summary>
        public void HideOverlay()
        {
            _overlayDependencies--;
            NotifyOfPropertyChange(() => IsOverlayVisible);
        }

        private void ReloadResolver(DnsCryptProxyType dnsCryptProxyType)
        {
            if (dnsCryptProxyType == DnsCryptProxyType.Primary)
            {
                if (_primaryResolver != null)
                {
                    PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter = ConvertProxyEntryToParameter(
                        _primaryResolver, DnsCryptProxyType.Primary);
                    if (PrimaryDnsCryptProxyManager.WriteRegistry(DnsCryptProxyType.Primary))
                    {
                        if (PrimaryDnsCryptProxyManager.IsDnsCryptProxyInstalled())
                        {
                            RestartService(DnsCryptProxyType.Primary);
                        }
                    }
                }
            }
            else
            {
                if (_secondaryResolver != null)
                {
                    SecondaryDnsCryptProxyManager.DnsCryptProxy.Parameter =
                        ConvertProxyEntryToParameter(_secondaryResolver, DnsCryptProxyType.Secondary);
                    if (SecondaryDnsCryptProxyManager.WriteRegistry(DnsCryptProxyType.Secondary))
                    {
                        if (SecondaryDnsCryptProxyManager.IsDnsCryptProxyInstalled())
                        {
                            RestartService(DnsCryptProxyType.Secondary);
                        }
                    }
                }
            }
        }

        private DnsCryptProxyParameter ConvertProxyEntryToParameter(DnsCryptProxyEntry dnsCryptProxyEntry,
            DnsCryptProxyType dnsCryptProxyType)
        {
            var dnsCryptProxyParameter = new DnsCryptProxyParameter
            {
                ProviderKey = dnsCryptProxyEntry.ProviderPublicKey,
                Plugins = Plugins.ToArray(),
                ProviderName = dnsCryptProxyEntry.ProviderName,
                ResolverAddress = dnsCryptProxyEntry.ResolverAddress,
                ResolverName = dnsCryptProxyEntry.Name,
                ResolversList =
                    Path.Combine(Directory.GetCurrentDirectory(), Global.DnsCryptProxyFolder,
                        Global.DnsCryptProxyResolverListName),
                EphemeralKeys = true,
                TcpOnly = UseTcpOnly
            };

            if (dnsCryptProxyType == DnsCryptProxyType.Primary)
            {
                if (ActAsGlobalGateway)
                {
                    dnsCryptProxyParameter.LocalAddress = Global.GlobalGatewayAddress;
                }
                else
                {
                    dnsCryptProxyParameter.LocalAddress = Global.PrimaryResolverAddress;
                }
            }
            else
            {
                dnsCryptProxyParameter.LocalAddress = Global.SecondaryResolverAddress;
            }

            return dnsCryptProxyParameter;
        }

        private async void RestartService(DnsCryptProxyType dnsCryptProxyType)
        {
            if (dnsCryptProxyType == DnsCryptProxyType.Primary)
            {
                IsWorkingOnPrimaryService = true;
                await Task.Run(() => { PrimaryDnsCryptProxyManager.Restart(); }).ConfigureAwait(false);
                Thread.Sleep(Global.ServiceRestartTime);
                _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
                IsWorkingOnPrimaryService = false;
            }
            else
            {
                IsWorkingOnSecondaryService = true;
                await Task.Run(() => { SecondaryDnsCryptProxyManager.Restart(); }).ConfigureAwait(false);
                Thread.Sleep(Global.ServiceRestartTime);
                _isSecondaryResolverRunning = SecondaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                NotifyOfPropertyChange(() => IsSecondaryResolverRunning);
                IsWorkingOnSecondaryService = false;
            }
        }

        private async void HandleService(DnsCryptProxyType dnsCryptProxyType)
        {
            if (dnsCryptProxyType == DnsCryptProxyType.Primary)
            {
                IsWorkingOnPrimaryService = true;
                if (IsPrimaryResolverRunning)
                {
                    // service is running, stop it
                    await Task.Run(() => { PrimaryDnsCryptProxyManager.Stop(); }).ConfigureAwait(false);
                    Thread.Sleep(Global.ServiceStopTime);
                    _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                    NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
                }
                else
                {
                    if (PrimaryDnsCryptProxyManager.IsDnsCryptProxyInstalled())
                    {
                        // service is installed, just start them
                        await Task.Run(() => { PrimaryDnsCryptProxyManager.Start(); }).ConfigureAwait(false);
                        Thread.Sleep(Global.ServiceStartTime);
                        _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                        NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
                    }
                    else
                    {
                        //install and start the service
                        var installResult =
                            await
                                Task.Run(() => { return PrimaryDnsCryptProxyManager.Install(); }).ConfigureAwait(false);
                        Thread.Sleep(Global.ServiceStartTime);
                        _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                        NotifyOfPropertyChange(() => IsPrimaryResolverRunning);

                        if (IsDebugModeEnabled)
                        {
                            if (installResult.Success)
                            {
                                _windowManager.ShowMetroMessageBox(
                                    installResult.StandardOutput,
                                    LocalizationEx.GetUiString("dialog_success_title",
                                        Thread.CurrentThread.CurrentCulture),
                                    MessageBoxButton.OK, BoxType.Default);
                            }
                            else
                            {
                                _windowManager.ShowMetroMessageBox(
                                    installResult.StandardOutput,
                                    LocalizationEx.GetUiString("dialog_error_title", Thread.CurrentThread.CurrentCulture),
                                    MessageBoxButton.OK, BoxType.Error);
                            }
                        }
                    }
                }
                IsWorkingOnPrimaryService = false;
            }
            else
            {
                IsWorkingOnSecondaryService = true;
                if (IsSecondaryResolverRunning)
                {
                    // service is running, stop it
                    await Task.Run(() => { SecondaryDnsCryptProxyManager.Stop(); }).ConfigureAwait(false);
                    Thread.Sleep(Global.ServiceStopTime);
                    _isSecondaryResolverRunning = SecondaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                    NotifyOfPropertyChange(() => IsSecondaryResolverRunning);
                }
                else
                {
                    if (SecondaryDnsCryptProxyManager.IsDnsCryptProxyInstalled())
                    {
                        // service is installed, just start them
                        await Task.Run(() => { SecondaryDnsCryptProxyManager.Start(); }).ConfigureAwait(false);
                        Thread.Sleep(Global.ServiceStartTime);
                        _isSecondaryResolverRunning = SecondaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                        NotifyOfPropertyChange(() => IsSecondaryResolverRunning);
                    }
                    else
                    {
                        //install and start the service
                        var installResult =
                            await
                                Task.Run(() => { return SecondaryDnsCryptProxyManager.Install(); })
                                    .ConfigureAwait(false);
                        Thread.Sleep(Global.ServiceStartTime);
                        _isSecondaryResolverRunning = SecondaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                        NotifyOfPropertyChange(() => IsSecondaryResolverRunning);

                        if (IsDebugModeEnabled)
                        {
                            if (installResult.Success)
                            {
                                _windowManager.ShowMetroMessageBox(
                                    installResult.StandardOutput,
                                    LocalizationEx.GetUiString("dialog_success_title",
                                        Thread.CurrentThread.CurrentCulture),
                                    MessageBoxButton.OK, BoxType.Default);
                            }
                            else
                            {
                                _windowManager.ShowMetroMessageBox(
                                    installResult.StandardOutput,
                                    LocalizationEx.GetUiString("dialog_error_title", Thread.CurrentThread.CurrentCulture),
                                    MessageBoxButton.OK, BoxType.Error);
                            }
                        }
                    }
                }
                IsWorkingOnSecondaryService = false;
            }
        }

        private void LoadNetworkCards()
        {
            var localNetworkInterfaces = LocalNetworkInterfaceManager.GetLocalNetworkInterfaces(ShowHiddenCards);
            _localNetworkInterfaces.Clear();
            if (localNetworkInterfaces.Count != 0)
            {
                foreach (var localNetworkInterface in localNetworkInterfaces)
                {
                    _localNetworkInterfaces.Add(localNetworkInterface);
                }
            }
        }

        public void NetworkCardClicked(LocalNetworkInterface localNetworkInterface)
        {
            if (localNetworkInterface == null) return;
            if (localNetworkInterface.UseDnsCrypt)
            {
                LocalNetworkInterfaceManager.SetNameservers(localNetworkInterface, new List<string>(),
                    NetworkInterfaceComponent.IPv4);
                localNetworkInterface.UseDnsCrypt = false;
            }
            else
            {
                var dns = new List<string>();
                if (PrimaryResolver != null)
                {
                    if (!string.IsNullOrEmpty(PrimaryResolver.ProviderPublicKey))
                    {
                        if (PrimaryDnsCryptProxyManager.DnsCryptProxy.IsReady)
                        {
                            dns.Add(Global.PrimaryResolverAddress);
                        }
                    }
                }
                if (SecondaryResolver != null)
                {
                    if (!string.IsNullOrEmpty(SecondaryResolver.ProviderPublicKey))
                    {
                        if (SecondaryDnsCryptProxyManager.DnsCryptProxy.IsReady)
                        {
                            dns.Add(Global.SecondaryResolverAddress);
                        }
                    }
                }
                LocalNetworkInterfaceManager.SetNameservers(localNetworkInterface, dns, NetworkInterfaceComponent.IPv4);
                localNetworkInterface.UseDnsCrypt = true;
            }
        }

        #region Helper

        /// <summary>
        ///     Check if the current user has administrative privileges.
        /// </summary>
        /// <returns><c>true</c> if the user has administrative privileges, otherwise <c>false</c></returns>
        public static bool IsAdministrator()
        {
            try
            {
                return (new WindowsPrincipal(WindowsIdentity.GetCurrent()))
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        ///     Check the dnscrypt-proxy directory on completeness.
        /// </summary>
        /// <returns><c>true</c> if alle files are available, otherwise <c>false</c></returns>
        private static bool ValidateDnsCryptProxyFolder()
        {
            foreach (var proxyFile in Global.DnsCryptProxyFiles)
            {
                //TODO: we also could do a security check with some blake2b checksums
                if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), Global.DnsCryptProxyFolder,
                    proxyFile)))
                {
                    return false;
                }
            }
            return true;
        }

        #endregion

        #region Advanced Settings

        public async void UninstallServices()
        {
            var result = _windowManager.ShowMetroMessageBox(
                LocalizationEx.GetUiString("dialog_message_uninstall", Thread.CurrentThread.CurrentCulture),
                LocalizationEx.GetUiString("dialog_uninstall_title", Thread.CurrentThread.CurrentCulture),
                MessageBoxButton.YesNo, BoxType.Default);

            if (result == MessageBoxResult.Yes)
            {
                IsUninstallingServices = true;
                await Task.Run(() =>
                {
                    PrimaryDnsCryptProxyManager.Uninstall();
                    SecondaryDnsCryptProxyManager.Uninstall();
                }).ConfigureAwait(false);
                Thread.Sleep(Global.ServiceUninstallTime);
                IsUninstallingServices = false;
            }

            _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
            NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
            _isSecondaryResolverRunning = SecondaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
            NotifyOfPropertyChange(() => IsSecondaryResolverRunning);

            // recover the network interfaces
            foreach (var nic in LocalNetworkInterfaceManager.GetLocalNetworkInterfaces())
            {
                if (nic.UseDnsCrypt)
                {
                    LocalNetworkInterfaceManager.SetNameservers(nic, new List<string>(), NetworkInterfaceComponent.IPv4);
                    _localNetworkInterfaces.SingleOrDefault(n => n.Description.Equals(nic.Description)).UseDnsCrypt =
                        false;
                }
            }
        }

        public async void RefreshResolverList()
        {
            IsRefreshingResolverList = true;
            var state = await DnsCryptProxyListManager.UpdateResolverListAsync();
            await Task.Run(() =>
            {
                // we do this, to prevent excessive usage
                Thread.Sleep(2000);
            }).ConfigureAwait(false);
            if (state)
            {
                var proxyList = Path.Combine(Directory.GetCurrentDirectory(),
                    Global.DnsCryptProxyFolder, Global.DnsCryptProxyResolverListName);
                var proxyListSignature = Path.Combine(Directory.GetCurrentDirectory(),
                    Global.DnsCryptProxyFolder, Global.DnsCryptProxySignatureFileName);
                var dnsProxyList =
                    DnsCryptProxyListManager.ReadProxyList(proxyList, proxyListSignature, true);
                if (dnsProxyList != null && dnsProxyList.Any())
                {
                    foreach (var dnsProxy in dnsProxyList)
                    {
                        if (
                            dnsProxy.ProviderPublicKey.Equals(
                                PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.ProviderKey))
                        {
                            _primaryResolver = dnsProxy;
                        }
                        if (
                            dnsProxy.ProviderPublicKey.Equals(
                                SecondaryDnsCryptProxyManager.DnsCryptProxy.Parameter.ProviderKey))
                        {
                            _secondaryResolver = dnsProxy;
                        }
                        _resolvers.Add(dnsProxy);
                    }
                }
            }
            else
            {
                _windowManager.ShowMetroMessageBox(
                    LocalizationEx.GetUiString("dialog_message_refresh_failed", Thread.CurrentThread.CurrentCulture),
                    LocalizationEx.GetUiString("dialog_warning_title", Thread.CurrentThread.CurrentCulture),
                    MessageBoxButton.OK, BoxType.Warning);
            }
            IsRefreshingResolverList = false;
        }

        public bool ActAsGlobalGateway
        {
            get { return _actAsGlobalGateway; }
            set
            {
                _actAsGlobalGateway = value;
                HandleGlobalResolver(_actAsGlobalGateway);
                NotifyOfPropertyChange(() => ActAsGlobalGateway);
            }
        }

        private async void HandleGlobalResolver(bool actAsGlobalGateway)
        {
            IsWorkingOnPrimaryService = true;
            if (actAsGlobalGateway)
            {
                PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.LocalAddress = Global.GlobalGatewayAddress;
                PrimaryDnsCryptProxyManager.WriteRegistry(DnsCryptProxyType.Primary);
                await Task.Run(() => { PrimaryDnsCryptProxyManager.Restart(); }).ConfigureAwait(false);
                Thread.Sleep(Global.ServiceRestartTime);
                _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
                PrimaryResolverTitle = string.Format("{0} ({1}:53)",
                    LocalizationEx.GetUiString("default_settings_primary_header", Thread.CurrentThread.CurrentCulture),
                    Global.GlobalGatewayAddress);
            }
            else
            {
                PrimaryDnsCryptProxyManager.DnsCryptProxy.Parameter.LocalAddress = Global.PrimaryResolverAddress;
                PrimaryDnsCryptProxyManager.WriteRegistry(DnsCryptProxyType.Primary);
                await Task.Run(() => { PrimaryDnsCryptProxyManager.Restart(); }).ConfigureAwait(false);
                Thread.Sleep(Global.ServiceRestartTime);
                _isPrimaryResolverRunning = PrimaryDnsCryptProxyManager.IsDnsCryptProxyRunning();
                NotifyOfPropertyChange(() => IsPrimaryResolverRunning);
                PrimaryResolverTitle = string.Format("{0} ({1}:{2})",
                    LocalizationEx.GetUiString("default_settings_primary_header", Thread.CurrentThread.CurrentCulture),
                    Global.PrimaryResolverAddress,
                    Global.PrimaryResolverPort);
            }
            IsWorkingOnPrimaryService = false;
        }

        public List<string> Plugins
        {
            get { return _plugins; }
            set
            {
                _plugins = value;
                NotifyOfPropertyChange(() => Plugins);
            }
        }

        public void OpenPluginManager()
        {
            var win = new PluginManagerViewModel
            {
                DisplayName = LocalizationEx.GetUiString("window_plugin_title", Thread.CurrentThread.CurrentCulture)
            };
            win.SetPlugins(Plugins);
            dynamic settings = new ExpandoObject();
            settings.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            settings.Owner = GetView();
            var inputOk = _windowManager.ShowDialog(win, null, settings);
            if (inputOk == true)
            {
                Plugins = win.Plugins;
                ReloadResolver(DnsCryptProxyType.Primary);
                ReloadResolver(DnsCryptProxyType.Secondary);
            }
        }

        public bool UseTcpOnly
        {
            get { return _useTcpOnly; }
            set
            {
                if (value.Equals(_useTcpOnly)) return;
                _useTcpOnly = value;
                ReloadResolver(DnsCryptProxyType.Primary);
                ReloadResolver(DnsCryptProxyType.Secondary);
                NotifyOfPropertyChange(() => UseTcpOnly);
            }
        }

        public bool IsRefreshingResolverList
        {
            get { return _isRefreshingResolverList; }
            set
            {
                _isRefreshingResolverList = value;
                NotifyOfPropertyChange(() => IsRefreshingResolverList);
            }
        }

        public bool IsUninstallingServices
        {
            get { return _isUninstallingServices; }
            set
            {
                _isUninstallingServices = value;
                NotifyOfPropertyChange(() => IsUninstallingServices);
            }
        }

        public bool IsDebugModeEnabled
        {
            get { return _isDebugModeEnabled; }
            set
            {
                _isDebugModeEnabled = value;
                NotifyOfPropertyChange(() => IsDebugModeEnabled);
            }
        }

        #endregion
    }
}
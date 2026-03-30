using System;
using System.Runtime.InteropServices;

namespace VideoPlayer
{
    internal static class VirtualDesktopPinner
    {
        private static readonly Guid CLSID_ImmersiveShell =
            new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        private static readonly Guid CLSID_VirtualDesktopPinnedApps =
            new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        private interface IServiceProvider10
        {
            [return: MarshalAs(UnmanagedType.IUnknown)]
            object QueryService(ref Guid service, ref Guid riid);
        }

        // IApplicationView inherits from IInspectable, which adds 3 methods
        // on top of IUnknown. We declare as IUnknown and add 3 padding methods.
        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
        private interface IApplicationView
        {
            // IInspectable methods (3 vtable slots)
            void GetIids();
            void GetRuntimeClassName();
            void GetTrustLevel();

            // IApplicationView methods
            int SetFocus();
            int SwitchTo();
            int TryInvokeBack(IntPtr callback);
            int GetThumbnailWindow(out IntPtr hwnd);
            int GetMonitor(out IntPtr monitor);
            int GetVisibility(out int visibility);
            int SetCloak(int cloakType, int unknown);
            int GetPosition(ref Guid guid, out IntPtr position);
            int SetPosition(ref IntPtr position);
            int InsertAfterWindow(IntPtr hwnd);
            int GetExtendedFramePosition(out long rect);
            int GetAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int SetAppUserModelId(string id);
            int IsEqualByAppUserModelId(string id, out int result);
            int GetViewState(out uint state);
            int SetViewState(uint state);
            int GetNeediness(out int neediness);
            int GetLastActivationTimestamp(out ulong timestamp);
            int SetLastActivationTimestamp(ulong timestamp);
            int GetVirtualDesktopId(out Guid guid);
            int SetVirtualDesktopId(ref Guid guid);
            int GetShowInSwitchers(out int flag);
            int SetShowInSwitchers(int flag);
            int GetScaleFactor(out int factor);
            int CanReceiveInput(out bool canReceiveInput);
            int GetCompatibilityPolicyType(out int flags);
            int SetCompatibilityPolicyType(int flags);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
        private interface IApplicationViewCollection
        {
            int GetViews(out IntPtr array);
            int GetViewsByZOrder(out IntPtr array);
            int GetViewsByAppUserModelId(string id, out IntPtr array);
            int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
            int GetViewForApplication(object application, out IApplicationView view);
            int GetViewForAppUserModelId(string id, out IApplicationView view);
            int GetViewInFocus(out IntPtr view);
            int Unknown1(out IntPtr view);
            void RefreshCollection();
            int RegisterForApplicationViewChanges(object listener, out int cookie);
            int UnregisterForApplicationViewChanges(int cookie);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
        private interface IVirtualDesktopPinnedApps
        {
            bool IsAppIdPinned(string appId);
            void PinAppID(string appId);
            void UnpinAppID(string appId);
            bool IsViewPinned(IApplicationView applicationView);
            void PinView(IApplicationView applicationView);
            void UnpinView(IApplicationView applicationView);
        }

        private static IApplicationViewCollection _viewCollection;
        private static IVirtualDesktopPinnedApps _pinnedApps;
        private static bool _initialized;
        private static bool _available;

        private static bool Initialize()
        {
            if (_initialized) return _available;
            _initialized = true;

            try
            {
                var shellType = Type.GetTypeFromCLSID(CLSID_ImmersiveShell);
                var shell = (IServiceProvider10)Activator.CreateInstance(shellType);

                var viewCollectionGuid = typeof(IApplicationViewCollection).GUID;
                _viewCollection = (IApplicationViewCollection)
                    shell.QueryService(ref viewCollectionGuid, ref viewCollectionGuid);

                var pinnedAppsClsid = CLSID_VirtualDesktopPinnedApps;
                var pinnedAppsIid = typeof(IVirtualDesktopPinnedApps).GUID;
                _pinnedApps = (IVirtualDesktopPinnedApps)
                    shell.QueryService(ref pinnedAppsClsid, ref pinnedAppsIid);

                _available = true;
            }
            catch
            {
                _available = false;
            }

            return _available;
        }

        public static bool PinWindow(IntPtr hWnd)
        {
            if (!Initialize()) return false;

            try
            {
                _viewCollection.GetViewForHwnd(hWnd, out var view);
                if (view != null && !_pinnedApps.IsViewPinned(view))
                {
                    _pinnedApps.PinView(view);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

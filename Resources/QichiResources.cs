using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DIYLevelFastInit
{

    internal static class QichiResources
    {
        private static MethodInfo _start, _get, _set;
        private static FieldInfo _request, _loaded, _loading;
        private static bool _resolved;
        private static bool _missingLogged;

        internal static bool IsRequired(string info)
        {
            return string.Equals(Path.GetFileName(info), "info_qichi", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Resolve()
        {
            if (_resolved) return true;
            Type runtime = null, loader = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                runtime = assembly.GetType("QichiRuntime.QichiRuntimePlugin", false);
                if (runtime == null) continue;
                loader = assembly.GetType("QichiRuntime.QichiExtraBundleLoader", false);
                break;
            }
            if (runtime == null)
            {
                if (!_missingLogged)
                    FastInitPlugin.Log.LogWarning("Qichi runtime is absent; qichi_assets background dependency cannot be prepared");
                _missingLogged = true;
                return false;
            }
            if (loader == null) throw new IOException("Unsupported Qichi runtime: missing resource loader");
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _start = runtime.GetMethod("TryLoadCustomBundlesOnce", flags, null, Type.EmptyTypes, null);
            _get = loader.GetMethod("GetLoadedQichi", flags, null, Type.EmptyTypes, null);
            _set = loader.GetMethod("SetLoaded", flags, null, new[] { typeof(AssetBundle) }, null);
            _request = runtime.GetField("s_bundleReq", flags);
            _loaded = runtime.GetField("s_bundleLoaded", flags);
            _loading = runtime.GetField("s_bundleLoading", flags);
            if (_start == null || _start.ReturnType != typeof(void) || _get == null || _get.ReturnType != typeof(AssetBundle)
                || _set == null || _set.ReturnType != typeof(void) || _request == null || _request.FieldType != typeof(AssetBundleCreateRequest)
                || _loaded == null || _loaded.FieldType != typeof(bool) || _loading == null || _loading.FieldType != typeof(bool))
                throw new IOException("Unsupported Qichi runtime resource loader; qichi_assets cannot be prepared safely");
            _resolved = true;
            return true;
        }

        private static AssetBundle FindLoaded()
        {
            AssetBundle bundle = _get.Invoke(null, null) as AssetBundle;
            if (bundle != null) return bundle;
            foreach (AssetBundle item in AssetBundle.GetAllLoadedAssetBundles())
                if (item != null && string.Equals(item.name, "qichi_assets", StringComparison.OrdinalIgnoreCase)) return item;
            return null;
        }

        private static AssetBundleCreateRequest Begin()
        {
            if (!Resolve()) return null;
            AssetBundleCreateRequest request = _request.GetValue(null) as AssetBundleCreateRequest;
            if (request != null) return request;
            AssetBundle bundle = FindLoaded();
            if (bundle != null) { Adopt(bundle, null); return null; }
            if ((bool)_loading.GetValue(null)) throw new IOException("Qichi runtime is loading without a resource request");
            _loaded.SetValue(null, false);
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            _start.Invoke(null, null);
            FastInitPlugin.Log.LogInfo("Qichi runtime request startup: " + timer.ElapsedMilliseconds + " ms before async wait");
            request = _request.GetValue(null) as AssetBundleCreateRequest;
            if (request == null) throw new IOException("Qichi runtime could not start loading qichi_assets");
            FastInitPlugin.Log.LogInfo("Qichi dependency loading: qichi_assets (runtime request)");
            return request;
        }

        private static void Adopt(AssetBundle bundle, AssetBundleCreateRequest request)
        {
            if (bundle == null) throw new IOException("Qichi dependency failed: qichi_assets");
            AssetBundleCreateRequest current = _request.GetValue(null) as AssetBundleCreateRequest;
            if (current != null && (!ReferenceEquals(current, request) || !current.isDone))
                throw new IOException("Qichi runtime still has an active resource request");
            bool changed = !ReferenceEquals(_get.Invoke(null, null), bundle) || !(bool)_loaded.GetValue(null);
            // Keep ownership in the runtime and stop its Update from reloading the same CAB.
            _set.Invoke(null, new object[] { bundle });
            _request.SetValue(null, null);
            _loading.SetValue(null, false);
            _loaded.SetValue(null, true);
            if (changed) FastInitPlugin.Log.LogInfo("Qichi dependency ready: qichi_assets (runtime-owned, before info/scene load)");
        }

        private static void Complete(AssetBundleCreateRequest request)
        {
            AssetBundle bundle = request.assetBundle;
            if (!request.isDone) throw new IOException("Qichi dependency request is still pending; no duplicate load was started");
            if (bundle == null) bundle = FindLoaded();
            Adopt(bundle, request);
        }

        internal static IEnumerator EnsureAsync()
        {
            AssetBundleCreateRequest request = Begin();
            if (request == null) yield break;
            yield return FastInitHost.WaitOp(request, "Qichi dependency qichi_assets");
            Complete(request);
        }

        internal static void Ensure()
        {
            AssetBundleCreateRequest request = Begin();
            if (request != null) Complete(request);
        }
    }
}

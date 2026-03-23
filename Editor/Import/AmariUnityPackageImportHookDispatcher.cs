using System;
using System.Collections.Generic;
using UnityEngine;

namespace com.amari_noa.unitypackage_pipeline_core.editor
{
    public sealed class AmariUnityPackageImportHookDispatcher
    {
        private readonly List<IAmariUnityPackageImportPipelineHook> _hooks = new();

        public void Register(IAmariUnityPackageImportPipelineHook hook)
        {
            if (hook == null || _hooks.Contains(hook))
            {
                return;
            }

            _hooks.Add(hook);
        }

        public void Unregister(IAmariUnityPackageImportPipelineHook hook)
        {
            if (hook == null)
            {
                return;
            }

            _hooks.Remove(hook);
        }

        public void DispatchBefore(AmariUnityPackageImportContext context)
        {
            Dispatch(hook => hook.OnBeforePackageImport(context), nameof(IAmariUnityPackageImportPipelineHook.OnBeforePackageImport));
        }

        public void DispatchCompleted(AmariUnityPackageImportContext context)
        {
            Dispatch(hook => hook.OnPackageImportCompleted(context), nameof(IAmariUnityPackageImportPipelineHook.OnPackageImportCompleted));
        }

        public void DispatchFinalized(AmariUnityPackageImportResultContext context)
        {
            Dispatch(hook => hook.OnImportRequestFinalized(context), nameof(IAmariUnityPackageImportPipelineHook.OnImportRequestFinalized));
        }

        private void Dispatch(Action<IAmariUnityPackageImportPipelineHook> invoker, string methodName)
        {
            if (invoker == null)
            {
                return;
            }

            var snapshot = _hooks.ToArray();
            foreach (var hook in snapshot)
            {
                if (hook == null)
                {
                    continue;
                }

                try
                {
                    invoker(hook);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{AmariUnityPackagePipelineLabels.LogPrefix} Hook \"{methodName}\" failed ({hook.GetType().Name}): {ex.Message}");
                }
            }
        }
    }
}

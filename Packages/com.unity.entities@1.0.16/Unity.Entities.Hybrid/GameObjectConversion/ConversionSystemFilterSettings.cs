using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Properties;
using Assembly = System.Reflection.Assembly;

namespace Unity.Entities.Conversion
{
#if UNITY_EDITOR
#if USING_PLATFORMS_PACKAGE
    public sealed class ConversionSystemFilterSettings : Unity.Build.IBuildComponent
    {
        HashSet<Assembly> m_ExcludedDomainAssemblies;

        // this must be initialized to true, so that when properties does a transfer
        // and updates the List<string> property, we get a chance to tell m_ConversionTypeCache
        // about the change.
        bool m_IsDirty = true;

        [CreateProperty]
        public List<UnityEngine.LazyLoadReference<UnityEditorInternal.AssemblyDefinitionAsset>> ExcludedConversionSystemAssemblies { get; set; } =
            new List<UnityEngine.LazyLoadReference<UnityEditorInternal.AssemblyDefinitionAsset>>();

        public ConversionSystemFilterSettings() {}

        public ConversionSystemFilterSettings(params string[] excludedAssemblyDefinitionNames)
        {
            foreach (var name in excludedAssemblyDefinitionNames)
            {
                var asset = FindAssemblyDefinitionAssetByName(name);
                if (asset != null && asset)
                {
                    ExcludedConversionSystemAssemblies.Add(asset);
                }
            }
        }

        public ConversionSystemFilterSettings(params UnityEditorInternal.AssemblyDefinitionAsset[] excludedAssemblyDefinitionAssets)
        {
            foreach (var asset in excludedAssemblyDefinitionAssets)
            {
                if (asset != null && asset)
                {
                    ExcludedConversionSystemAssemblies.Add(asset);
                }
            }
        }

        public UnityEditorInternal.AssemblyDefinitionAsset FindAssemblyDefinitionAssetByName(string name)
        {
            var assetPath = UnityEditor.AssetDatabase.FindAssets($"t: asmdef {name}")
                .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(x => Path.GetFileNameWithoutExtension(x) == name);
            return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditorInternal.AssemblyDefinitionAsset>(assetPath);
        }

        public bool ShouldRunConversionSystem(Type type)
        {
            UpdateIfDirty();
            if (m_ExcludedDomainAssemblies == null)
                return true;

            return !m_ExcludedDomainAssemblies.Contains(type.Assembly);
        }

        public bool IsAssemblyExcluded(Assembly assembly)
        {
            UpdateIfDirty();
            if (m_ExcludedDomainAssemblies == null)
                return false;

            return m_ExcludedDomainAssemblies.Contains(assembly);
        }

        public void SetDirty()
        {
            m_IsDirty = true;
        }

        void UpdateIfDirty()
        {
            if (!m_IsDirty)
                return;

            if (ExcludedConversionSystemAssemblies.Count == 0)
            {
                m_ExcludedDomainAssemblies = null;
                return;
            }

            m_ExcludedDomainAssemblies = new HashSet<Assembly>();

            var domainAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var excl in ExcludedConversionSystemAssemblies.Select(lazy => lazy.asset))
            {
                var asm = domainAssemblies.FirstOrDefault(s => s.GetName().Name == excl.name);
                if (asm != null)
                    m_ExcludedDomainAssemblies.Add(asm);
            }

            m_IsDirty = false;
        }
    }
#endif
#endif
}

using System.Reflection;

namespace Fantasy.Helper
{
    /// <summary>
    /// AssemblyHelper
    /// </summary>
    public static class AssemblyHelper
    {
        private const BindingFlags EnsureLoadedFlags = BindingFlags.Public | BindingFlags.Static;

        /// <summary>
        /// 确保程序集已加载并初始化
        /// </summary>
        /// <param name="assembly">要加载的程序集</param>
        public static void EnsureLoaded(this System.Reflection.Assembly assembly)
        {
            var assemblyName = assembly.GetName().Name;

            if (string.IsNullOrEmpty(assemblyName))
            {
                return;
            }

            var markerClassName = assemblyName.Replace("-", "_").Replace(".", "_") + "_AssemblyInitializer";
            var fullTypeName = string.Concat("Fantasy.Generated.", markerClassName);
            var staticType = assembly.GetType(fullTypeName);
            
            if (staticType == null)
            {
                return;
            }

            var staticMethod = staticType.GetMethod("Initialize", EnsureLoadedFlags);
            
            if (staticMethod == null)
            {
                return;
            }
            
            staticMethod.Invoke(null, null);
        }

        /// <summary>
        /// 确保程序集已加载并等待框架生命周期注册完成
        /// </summary>
        /// <param name="assembly">要加载的程序集</param>
        public static async System.Threading.Tasks.Task EnsureLoadedAsync(this System.Reflection.Assembly assembly)
        {
            EnsureLoaded(assembly);

            if (Fantasy.Assembly.AssemblyManifest.TryGetLoadTask(assembly, out var loadTask))
            {
                await loadTask.ConfigureAwait(false);
            }
        }
    }
}
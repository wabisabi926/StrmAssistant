using System;
using System.Reflection;

namespace StrmAssistant.Tests.Support
{
    /// <summary>
    /// 轻量接口替身：用 DispatchProxy 拦截调用，按方法名返回预设值。
    /// 目的：在不引入 Moq 等依赖的前提下，为 Emby 那些成员很多的接口
    /// （IFileSystem 有 80+ 成员）提供最小实现。
    /// </summary>
    public class InterfaceProxy : DispatchProxy
    {
        internal Func<MethodInfo, object[], object> Handler;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var result = Handler?.Invoke(targetMethod, args);
            if (result != null || targetMethod.ReturnType == typeof(void)) return result;

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }

        public static T Create<T>(Func<MethodInfo, object[], object> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, InterfaceProxy>();
            ((InterfaceProxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}

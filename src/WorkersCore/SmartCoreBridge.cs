using System;
using System.Linq;
using System.Reflection;

namespace Valheim_Workers
{
    internal static class SmartCoreBridge
    {
        private static bool _registered;
        private static bool _reportedUnsupported;
        private static bool _reportedMissing;

        internal static bool IsRegistered => _registered;

        internal static bool TryRegister(
            string pageId,
            string title,
            int order,
            Action drawPage,
            Action<string> debugLog,
            Action<string> infoLog)
        {
            if (_registered) return true;

            try
            {
                Type registry = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Valheim_SmartCore.SmartSettingsRegistry", false))
                    .FirstOrDefault(t => t != null);

                if (registry == null)
                {
                    if (!_reportedMissing)
                    {
                        _reportedMissing = true;
                        debugLog?.Invoke("SmartCore not detected yet; Workers stays standalone and will retry briefly during startup.");
                    }
                    return false;
                }

                MethodInfo[] methods = registry
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => string.Equals(m.Name, "Register", StringComparison.Ordinal))
                    .OrderBy(m => m.GetParameters().Length)
                    .ToArray();

                foreach (MethodInfo method in methods)
                {
                    if (!TryBuildArguments(method, pageId, title, order, drawPage, out object[] args))
                        continue;

                    method.Invoke(null, args);
                    _registered = true;
                    infoLog?.Invoke("SmartCore settings page registered: Workers");
                    return true;
                }

                if (!_reportedUnsupported)
                {
                    _reportedUnsupported = true;
                    string signatures = methods.Length == 0
                        ? "none"
                        : string.Join(" | ", methods.Select(Describe));
                    debugLog?.Invoke("SmartCore detected, but no supported Register signature was found. Workers remains functional without a hard SmartCore dependency. Found: " + signatures);
                }
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException ?? ex;
                debugLog?.Invoke("SmartCore Workers-page registration failed safely: " + inner.GetType().Name + ": " + inner.Message);
            }
            catch (Exception ex)
            {
                debugLog?.Invoke("SmartCore Workers-page registration failed safely: " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        private static bool TryBuildArguments(MethodInfo method, string pageId, string title, int order, Action drawPage, out object[] args)
        {
            ParameterInfo[] parameters = method.GetParameters();
            args = null;

            // We intentionally support only simple page-registration overloads. If SmartCore changes,
            // Workers fails closed instead of guessing complex private API structures.
            if (parameters.Length < 3 || parameters.Length > 4) return false;

            int stringCount = parameters.Count(p => p.ParameterType == typeof(string));
            int intCount = parameters.Count(p => p.ParameterType == typeof(int));
            int delegateCount = parameters.Count(p => IsZeroArgVoidDelegate(p.ParameterType));
            if (stringCount != 2 || delegateCount != 1) return false;
            if (parameters.Length == 3 && intCount != 0) return false;
            if (parameters.Length == 4 && intCount != 1) return false;

            object[] built = new object[parameters.Length];
            bool idAssigned = false;
            bool titleAssigned = false;

            // First pass: honor informative parameter names when SmartCore exposes them.
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo p = parameters[i];
                if (p.ParameterType != typeof(string)) continue;
                string name = p.Name ?? string.Empty;

                if (name.IndexOf("id", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    built[i] = pageId;
                    idAssigned = true;
                }
                else if (name.IndexOf("title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    built[i] = title;
                    titleAssigned = true;
                }
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo p = parameters[i];
                Type type = p.ParameterType;

                if (type == typeof(string))
                {
                    if (built[i] == null)
                    {
                        if (!idAssigned)
                        {
                            built[i] = pageId;
                            idAssigned = true;
                        }
                        else if (!titleAssigned)
                        {
                            built[i] = title;
                            titleAssigned = true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    continue;
                }

                if (type == typeof(int))
                {
                    built[i] = order;
                    continue;
                }

                if (IsZeroArgVoidDelegate(type))
                {
                    if (type == typeof(Action))
                    {
                        built[i] = drawPage;
                    }
                    else
                    {
                        try
                        {
                            built[i] = Delegate.CreateDelegate(type, drawPage.Target, drawPage.Method);
                        }
                        catch
                        {
                            return false;
                        }
                    }
                    continue;
                }

                return false;
            }

            args = built;
            return true;
        }

        private static bool IsZeroArgVoidDelegate(Type type)
        {
            if (!typeof(Delegate).IsAssignableFrom(type)) return false;
            MethodInfo invoke = type.GetMethod("Invoke");
            return invoke != null && invoke.ReturnType == typeof(void) && invoke.GetParameters().Length == 0;
        }

        private static string Describe(MethodInfo method)
        {
            return method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
        }
    }
}

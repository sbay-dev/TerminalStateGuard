using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TSG;

/// <summary>
/// COM-based UI Automation helper for enumerating live terminal tabs.
/// Uses IUIAutomation COM interface directly — works in .NET 10 without framework references.
/// </summary>
[SupportedOSPlatform("windows")]
static class UiaComHelper
{
    // IUIAutomation COM CLSID and IID
    static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    static readonly Guid IID_IUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    // UI Automation property IDs
    const int UIA_ClassNamePropertyId = 30012;
    const int UIA_NamePropertyId = 30005;
    const int UIA_ControlTypePropertyId = 30003;
    const int UIA_TabItemControlTypeId = 50019;
    const int UIA_ProcessIdPropertyId = 30002;

    // TreeScope
    const int TreeScope_Children = 0x2;
    const int TreeScope_Descendants = 0x4;

    public static List<StateCapture.LiveWindowInfo>? GetTerminalWindowTabs()
    {
        var hr = NativeUia.CoCreateInstance(
            CLSID_CUIAutomation, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */,
            IID_IUIAutomation, out var pAutomation);

        if (hr != 0 || pAutomation == IntPtr.Zero) return null;

        try
        {
            return EnumerateTerminals(pAutomation);
        }
        finally
        {
            Marshal.Release(pAutomation);
        }
    }

    static List<StateCapture.LiveWindowInfo> EnumerateTerminals(IntPtr pAutomation)
    {
        var result = new List<StateCapture.LiveWindowInfo>();
        var automation = new UiaAutomation(pAutomation);

        var pRoot = automation.GetRootElement();
        if (pRoot == IntPtr.Zero) return result;

        try
        {
            // Create condition: ClassName == "CASCADIA_HOSTING_WINDOW_CLASS"
            var pClassCond = automation.CreatePropertyCondition(UIA_ClassNamePropertyId, "CASCADIA_HOSTING_WINDOW_CLASS");
            if (pClassCond == IntPtr.Zero) return result;

            try
            {
                var pWindows = FindAll(pRoot, TreeScope_Children, pClassCond);
                if (pWindows == IntPtr.Zero) return result;

                try
                {
                    var windowCount = GetArrayLength(pWindows);
                    for (var i = 0; i < windowCount; i++)
                    {
                        var pWindow = GetArrayElement(pWindows, i);
                        if (pWindow == IntPtr.Zero) continue;

                        try
                        {
                            var windowTitle = GetElementName(pWindow);
                            var windowPid = GetElementProcessId(pWindow);

                            // Create condition: ControlType == TabItem
                            var pTabCond = automation.CreatePropertyConditionInt(UIA_ControlTypePropertyId, UIA_TabItemControlTypeId);
                            if (pTabCond == IntPtr.Zero) continue;

                            try
                            {
                                var pTabs = FindAll(pWindow, TreeScope_Descendants, pTabCond);
                                if (pTabs == IntPtr.Zero) continue;

                                try
                                {
                                    var tabNames = new List<string>();
                                    var tabCount = GetArrayLength(pTabs);
                                    for (var j = 0; j < tabCount; j++)
                                    {
                                        var pTab = GetArrayElement(pTabs, j);
                                        if (pTab == IntPtr.Zero) continue;

                                        try
                                        {
                                            tabNames.Add(GetElementName(pTab));
                                        }
                                        finally
                                        {
                                            Marshal.Release(pTab);
                                        }
                                    }

                                    result.Add(new StateCapture.LiveWindowInfo(windowTitle, tabNames, windowPid));
                                }
                                finally
                                {
                                    Marshal.Release(pTabs);
                                }
                            }
                            finally
                            {
                                Marshal.Release(pTabCond);
                            }
                        }
                        finally
                        {
                            Marshal.Release(pWindow);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(pWindows);
                }
            }
            finally
            {
                Marshal.Release(pClassCond);
            }
        }
        finally
        {
            Marshal.Release(pRoot);
        }

        return result;
    }

    // IUIAutomationElement::FindAll (vtable index 6)
    static IntPtr FindAll(IntPtr pElement, int scope, IntPtr pCondition)
    {
        var vtable = Marshal.ReadIntPtr(pElement);
        // IUIAutomationElement vtable: QI, AddRef, Release, SetFocus, GetRuntimeId, FindFirst(5), FindAll(6)
        var pfn = Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<FindAllDelegate>(pfn);
        var hr = fn(pElement, scope, pCondition, out var pFound);
        return hr == 0 ? pFound : IntPtr.Zero;
    }

    // IUIAutomationElementArray::get_Length (vtable index 3)
    static int GetArrayLength(IntPtr pArray)
    {
        var vtable = Marshal.ReadIntPtr(pArray);
        var pfn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetLengthDelegate>(pfn);
        var hr = fn(pArray, out var length);
        return hr == 0 ? length : 0;
    }

    // IUIAutomationElementArray::GetElement (vtable index 4)
    static IntPtr GetArrayElement(IntPtr pArray, int index)
    {
        var vtable = Marshal.ReadIntPtr(pArray);
        var pfn = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetElementDelegate>(pfn);
        var hr = fn(pArray, index, out var pElement);
        return hr == 0 ? pElement : IntPtr.Zero;
    }

    // IUIAutomationElement::get_CurrentName (vtable index 23)
    static string GetElementName(IntPtr pElement)
    {
        var vtable = Marshal.ReadIntPtr(pElement);
        var pfn = Marshal.ReadIntPtr(vtable, 23 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetNameDelegate>(pfn);
        var hr = fn(pElement, out var bstrName);
        if (hr != 0 || bstrName == IntPtr.Zero) return "";
        var name = Marshal.PtrToStringBSTR(bstrName);
        Marshal.FreeBSTR(bstrName);
        return name ?? "";
    }

    /// <summary>Get ProcessId from IUIAutomationElement (vtable index 20: get_CurrentProcessId)</summary>
    static int GetElementProcessId(IntPtr pElement)
    {
        var vtable = Marshal.ReadIntPtr(pElement);
        // IUIAutomationElement::get_CurrentProcessId is at vtable index 20
        var pfn = Marshal.ReadIntPtr(vtable, 20 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetProcessIdDelegate>(pfn);
        var hr = fn(pElement, out var pid);
        return hr == 0 ? pid : 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int FindAllDelegate(IntPtr self, int scope, IntPtr condition, out IntPtr found);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetLengthDelegate(IntPtr self, out int length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetElementDelegate(IntPtr self, int index, out IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetNameDelegate(IntPtr self, out IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetProcessIdDelegate(IntPtr self, out int pid);

    // Wrapper for IUIAutomation interface
    readonly struct UiaAutomation(IntPtr pAutomation)
    {
        // IUIAutomation::GetRootElement (vtable index 5)
        public IntPtr GetRootElement()
        {
            var vtable = Marshal.ReadIntPtr(pAutomation);
            var pfn = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<GetRootElementDelegate>(pfn);
            var hr = fn(pAutomation, out var pRoot);
            return hr == 0 ? pRoot : IntPtr.Zero;
        }

        // IUIAutomation::CreatePropertyCondition (vtable index 23 for string variant)
        public IntPtr CreatePropertyCondition(int propertyId, string value)
        {
            var vtable = Marshal.ReadIntPtr(pAutomation);
            // IUIAutomation::CreatePropertyCondition is at vtable index 23
            var pfn = Marshal.ReadIntPtr(vtable, 23 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<CreatePropertyConditionDelegate>(pfn);

            // Create VARIANT for BSTR
            var variant = new NativeUia.VARIANT();
            variant.vt = 8; // VT_BSTR
            variant.data = Marshal.StringToBSTR(value);

            try
            {
                var hr = fn(pAutomation, propertyId, variant, out var pCondition);
                return hr == 0 ? pCondition : IntPtr.Zero;
            }
            finally
            {
                if (variant.data != IntPtr.Zero)
                    Marshal.FreeBSTR(variant.data);
            }
        }

        // CreatePropertyCondition for int values
        public IntPtr CreatePropertyConditionInt(int propertyId, int value)
        {
            var vtable = Marshal.ReadIntPtr(pAutomation);
            var pfn = Marshal.ReadIntPtr(vtable, 23 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<CreatePropertyConditionDelegate>(pfn);

            var variant = new NativeUia.VARIANT();
            variant.vt = 3; // VT_I4
            variant.data = (IntPtr)value;

            var hr = fn(pAutomation, propertyId, variant, out var pCondition);
            return hr == 0 ? pCondition : IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetRootElementDelegate(IntPtr self, out IntPtr root);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int CreatePropertyConditionDelegate(IntPtr self, int propertyId, NativeUia.VARIANT value, out IntPtr condition);
    }
}

static partial class NativeUia
{
    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        in Guid riid, out IntPtr ppv);

    [StructLayout(LayoutKind.Sequential)]
    public struct VARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr data;
        public IntPtr data2;
    }
}

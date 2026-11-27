using System.Runtime.InteropServices;
using GameCenter.Core.Util;

namespace GameCenter.Core.Config;

/// <summary>
/// Vulkan có dùng được thật không. Chỉ có bộ nạp (vulkan-1.dll / libvulkan.so.1) là chưa đủ: máy ảo, Remote Desktop,
/// card đồ họa cũ có bộ nạp nhưng không có driver Vulkan → RetroArch báo "Cannot open video driver" rồi thoát,
/// game PS1 không mở được (máy Windows sạch của CI gặp đúng như vậy).
/// Kiểm tra giống RetroArch: có VK_KHR_surface + surface của hệ điều hành, tạo được instance và có ít nhất một card đồ họa.
/// Kết quả giữ cho cả phiên chạy.
/// </summary>
public static class VulkanProbe
{
    private const int VkSuccess = 0;
    private const int VkIncomplete = 5;
    private const int VkStructureTypeInstanceCreateInfo = 1;
    /// <summary>sizeof(VkExtensionProperties): char extensionName[256] + uint32_t specVersion.</summary>
    private const int ExtensionPropertiesSize = 260;

    private static readonly object Gate = new();
    private static readonly Dictionary<Os, bool> Results = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumerateInstanceExtensionProperties(IntPtr layerName, ref uint count, IntPtr properties);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateInstance(ref VkInstanceCreateInfo info, IntPtr allocator, out IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void DestroyInstance(IntPtr instance, IntPtr allocator);

    /// <summary>Vulkan dùng được trên máy này (chỉ kiểm tra được cho hệ điều hành đang chạy; hệ khác → false).</summary>
    public static bool IsUsable(Os os)
    {
        if (os != OsPlatform.Current) return false;
        lock (Gate)
        {
            if (Results.TryGetValue(os, out var known)) return known;
            var ok = Probe(os, out var reason);
            Log.Info($"Vulkan: {(ok ? "dùng được" : "không dùng (" + reason + ")")}");
            return Results[os] = ok;
        }
    }

    private static bool Probe(Os os, out string reason)
    {
        var (library, surfaces) = os switch
        {
            Os.Windows => ("vulkan-1.dll", new[] { "VK_KHR_win32_surface" }),
            Os.Linux => ("libvulkan.so.1", new[] { "VK_KHR_xlib_surface", "VK_KHR_xcb_surface", "VK_KHR_wayland_surface" }),
            // macOS: không có Vulkan gốc (MoltenVK không đi kèm)
            _ => (null, Array.Empty<string>()),
        };
        reason = "không có bộ nạp Vulkan";
        if (library == null || !NativeLibrary.TryLoad(library, out var lib)) return false;
        try
        {
            var extensions = InstanceExtensions(Export<EnumerateInstanceExtensionProperties>(lib, "vkEnumerateInstanceExtensionProperties"));
            if (!extensions.Contains("VK_KHR_surface") || !surfaces.Any(extensions.Contains))
            {
                reason = "không có driver Vulkan vẽ được lên cửa sổ";
                return false;
            }

            var info = new VkInstanceCreateInfo { SType = VkStructureTypeInstanceCreateInfo };
            if (Export<CreateInstance>(lib, "vkCreateInstance")(ref info, IntPtr.Zero, out var instance) != VkSuccess || instance == IntPtr.Zero)
            {
                reason = "không tạo được Vulkan instance";
                return false;
            }
            try
            {
                uint devices = 0;
                int r = Export<EnumeratePhysicalDevices>(lib, "vkEnumeratePhysicalDevices")(instance, ref devices, IntPtr.Zero);
                reason = "không có card đồ họa hỗ trợ Vulkan";
                return r is VkSuccess or VkIncomplete && devices > 0;
            }
            finally { Export<DestroyInstance>(lib, "vkDestroyInstance")(instance, IntPtr.Zero); }
        }
        catch (Exception e)
        {
            reason = e.GetType().Name;
            return false;
        }
        finally { NativeLibrary.Free(lib); }
    }

    private static T Export<T>(IntPtr lib, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    private static HashSet<string> InstanceExtensions(EnumerateInstanceExtensionProperties enumerate)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        uint count = 0;
        if (enumerate(IntPtr.Zero, ref count, IntPtr.Zero) != VkSuccess || count == 0) return names;
        var buffer = Marshal.AllocHGlobal((int)count * ExtensionPropertiesSize);
        try
        {
            int r = enumerate(IntPtr.Zero, ref count, buffer);
            if (r is not (VkSuccess or VkIncomplete)) return names;
            for (int i = 0; i < count; i++)
                if (Marshal.PtrToStringAnsi(buffer + i * ExtensionPropertiesSize) is { Length: > 0 } name) names.Add(name);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return names;
    }
}

#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Pinta;

internal static class WindowsWindowPlacement
{
	private const uint MONITOR_DEFAULTTONULL = 0;
	private const uint MONITOR_DEFAULTTONEAREST = 2;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_NOZORDER = 0x0004;
	private const uint GW_OWNER = 4;

	internal static void Restore (int x, int y, int width, int height)
	{
		if (x == int.MinValue || y == int.MinValue)
			return;

		IntPtr window = FindMainWindow ();
		if (window == IntPtr.Zero)
			return;

		Rect bounds = new (x, y, x + Math.Max (1, width), y + Math.Max (1, height));
		IntPtr monitor = MonitorFromRect (ref bounds, MONITOR_DEFAULTTONULL);
		if (monitor == IntPtr.Zero)
			monitor = MonitorFromRect (ref bounds, MONITOR_DEFAULTTONEAREST);

		MonitorInfo info = new () { Size = Marshal.SizeOf<MonitorInfo> () };
		if (monitor == IntPtr.Zero || !GetMonitorInfo (monitor, ref info))
			return;

		Rect visible = FitToWorkArea (bounds, info.WorkArea);
		_ = SetWindowPos (
			window,
			IntPtr.Zero,
			visible.Left,
			visible.Top,
			visible.Width,
			visible.Height,
			SWP_NOACTIVATE | SWP_NOZORDER);
	}

	internal static bool TryGetNormalBounds (out Rect bounds)
	{
		bounds = default;
		IntPtr window = FindMainWindow ();
		WindowPlacement placement = new () { Length = Marshal.SizeOf<WindowPlacement> () };
		if (window == IntPtr.Zero || !GetWindowPlacement (window, ref placement))
			return false;

		bounds = placement.NormalPosition;
		return bounds.Width > 0 && bounds.Height > 0;
	}

	private static Rect FitToWorkArea (Rect bounds, Rect workArea)
	{
		int width = Math.Min (bounds.Width, workArea.Width);
		int height = Math.Min (bounds.Height, workArea.Height);
		int left = Math.Clamp (bounds.Left, workArea.Left, workArea.Right - width);
		int top = Math.Clamp (bounds.Top, workArea.Top, workArea.Bottom - height);
		return new Rect (left, top, left + width, top + height);
	}

	private static IntPtr FindMainWindow ()
	{
		IntPtr result = IntPtr.Zero;
		uint currentProcessId = (uint) Environment.ProcessId;

		_ = EnumWindows ((window, state) => {
			_ = GetWindowThreadProcessId (window, out uint processId);
			if (processId != currentProcessId || !IsWindowVisible (window) || GetWindow (window, GW_OWNER) != IntPtr.Zero)
				return true;

			result = window;
			return false;
		}, IntPtr.Zero);

		return result;
	}

	[StructLayout (LayoutKind.Sequential)]
	internal readonly record struct Rect (int Left, int Top, int Right, int Bottom)
	{
		internal int Width => Right - Left;
		internal int Height => Bottom - Top;
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct Point
	{
		internal int X;
		internal int Y;
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct WindowPlacement
	{
		internal int Length;
		internal int Flags;
		internal int ShowCommand;
		internal Point MinPosition;
		internal Point MaxPosition;
		internal Rect NormalPosition;
	}

	[StructLayout (LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		internal int Size;
		internal Rect Monitor;
		internal Rect WorkArea;
		internal uint Flags;
	}

	private delegate bool EnumWindowsCallback (IntPtr window, IntPtr state);

	[DllImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static extern bool EnumWindows (EnumWindowsCallback callback, IntPtr state);

	[DllImport ("user32.dll")]
	private static extern IntPtr GetWindow (IntPtr window, uint command);

	[DllImport ("user32.dll")]
	private static extern uint GetWindowThreadProcessId (IntPtr window, out uint processId);

	[DllImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static extern bool IsWindowVisible (IntPtr window);

	[DllImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static extern bool GetWindowPlacement (IntPtr window, ref WindowPlacement placement);

	[DllImport ("user32.dll")]
	private static extern IntPtr MonitorFromRect (ref Rect bounds, uint flags);

	[DllImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo (IntPtr monitor, ref MonitorInfo info);

	[DllImport ("user32.dll")]
	[return: MarshalAs (UnmanagedType.Bool)]
	private static extern bool SetWindowPos (
		IntPtr window,
		IntPtr insertAfter,
		int x,
		int y,
		int width,
		int height,
		uint flags);
}
#endif

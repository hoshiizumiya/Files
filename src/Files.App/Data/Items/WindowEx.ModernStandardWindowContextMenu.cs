// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;

namespace Files.App.Data.Items
{
	/// <summary>
	/// Displays the native Windows system menu using WinUI controls.
	/// </summary>
	public unsafe partial class WindowEx
	{
		private static readonly uint[] StandardCommands =
		[
			PInvoke.SC_RESTORE,
			PInvoke.SC_MOVE,
			PInvoke.SC_SIZE,
			PInvoke.SC_MINIMIZE,
			PInvoke.SC_MAXIMIZE,
			PInvoke.SC_CLOSE
		];

		private readonly Dictionary<uint, MenuFlyoutItem> _standardSystemMenuItems = [];

		private MenuFlyout? _modernSystemMenu;
		private ResourceDictionary? _modernSystemMenuResources;

		/// <summary>
		/// Handles system menu requests while preserving native fallback behavior.
		/// </summary>
		private bool TryHandleModernSystemMenuMessage(uint message, WPARAM wParam, LPARAM lParam)
		{
			try
			{
				HWND hwnd = new(WindowHandle);

				if (message == PInvoke.WM_SYSCOMMAND)
				{
					// Handle Alt+Space.
					if (((uint)wParam.Value & 0xFFF0u) != PInvoke.SC_KEYMENU ||
						lParam.Value != (nint)(ushort)VIRTUAL_KEY.VK_SPACE ||
						PInvoke.IsIconic(hwnd))
						return false;

					return TryShowModernSystemMenu(null);
				}

				if (message == PInvoke.WM_NCRBUTTONUP)
				{
					uint hitTest = (uint)wParam.Value;

					if (hitTest != PInvoke.HTCAPTION &&
						hitTest != PInvoke.HTSYSMENU)
						return false;

					// Always request the modern menu.
					// FlyoutBase.ShowAt handles an already-open flyout.
					return TryShowModernSystemMenu(lParam.Value);
				}

				if (message == PInvoke.WM_CONTEXTMENU)
				{
					if ((nint)wParam.Value != WindowHandle || lParam.Value == -1)
						return false;

					LRESULT hitTest = PInvoke.SendMessage(
						hwnd,
						PInvoke.WM_NCHITTEST,
						default,
						lParam);

					if (hitTest.Value != (nint)PInvoke.HTCAPTION &&
						hitTest.Value != (nint)PInvoke.HTSYSMENU)
						return false;

					return TryShowModernSystemMenu(lParam.Value);
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Modern system menu unavailable: {ex}");
			}

			return false;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private bool TryShowModernSystemMenu(nint? screenCoordinates)
		{
			if (Content is not FrameworkElement target || target.XamlRoot is null)
				return false;

			HWND hwnd = new(WindowHandle);
			HMENU systemMenu = PInvoke.GetSystemMenu(hwnd, false);

			if (systemMenu.IsNull || PInvoke.GetMenuItemCount(systemMenu) < 2)
				return false;

			// Allow the window to update the native menu before reading its state.
			// The system menu belongs to Windows and must not be destroyed here.
			PInvoke.SendMessage(hwnd, PInvoke.WM_INITMENU, (WPARAM)(nuint)systemMenu.Value, default);

			var menu = GetOrCreateModernSystemMenu();

			// Rebuild menu items only when the flyout is closed.
			// An open flyout already contains the current native menu snapshot.
			if (!menu.IsOpen && !TryRefreshModernSystemMenu(menu, systemMenu))
				return false;

			uint dpi = PInvoke.GetDpiForWindow(hwnd);
			float dpiScale = dpi == 0 ? 1f : 96f / dpi;
			Point position;

			if (screenCoordinates is nint packed)
			{
				long value = (long)packed;

				// Window messages store signed screen coordinates in lParam.
				System.Drawing.Point screenPoint = new(
					unchecked((short)(value & 0xFFFF)),
					unchecked((short)((value >> 16) & 0xFFFF)));

				if (!PInvoke.ScreenToClient(hwnd, ref screenPoint))
					return false;

				position = new Point(screenPoint.X * dpiScale, screenPoint.Y * dpiScale);
			}
			else
			{
				float titleBarHeight = ExtendsContentIntoTitleBar
					? AppWindow.TitleBar.Height * dpiScale
					: 0f;

				position = new Point(0, titleBarHeight);
			}

			menu.XamlRoot = target.XamlRoot;
			menu.ShowAt(target, new FlyoutShowOptions
			{
				Position = position,
				ShowMode = FlyoutShowMode.Standard
			});

			return true;
		}

		[DynamicWindowsRuntimeCast(typeof(MenuFlyout))]
		[DynamicWindowsRuntimeCast(typeof(MenuFlyoutItem))]
		private MenuFlyout GetOrCreateModernSystemMenu()
		{
			if (_modernSystemMenu is not null)
				return _modernSystemMenu;

			_modernSystemMenuResources = new ResourceDictionary
			{
				Source = new Uri("ms-appx:///Styles/ModernStandardWindowContextMenu.xaml")
			};

			var menu = (MenuFlyout)_modernSystemMenuResources["WindowExModernSystemMenu"];

			// Cache the existing XAML items to retain their icons and styles.
			for (int index = 0; index < StandardCommands.Length; index++)
			{
				int menuIndex = index == StandardCommands.Length - 1
					? menu.Items.Count - 1
					: index;

				_standardSystemMenuItems.Add(
					StandardCommands[index],
					(MenuFlyoutItem)menu.Items[menuIndex]);
			}

			// The visible items are reconstructed from the native menu.
			menu.Items.Clear();

			_modernSystemMenu = menu;
			return menu;
		}

		private bool TryRefreshModernSystemMenu(MenuFlyout menu, HMENU systemMenu)
		{
			int count = PInvoke.GetMenuItemCount(systemMenu);
			if (count < 2)
				return false;

			var nativeItems = new List<(uint Command, string Text, MENU_ITEM_TYPE Type, MENU_ITEM_STATE State)>(count);

			// Read all entries before changing the visible menu.
			// Unsupported native items must fall back to the Windows menu.
			for (uint index = 0; index < (uint)count; index++)
			{
				if (!TryReadNativeMenuItem(systemMenu, index, out MENUITEMINFOW info, out string text))
					return false;

				if (!info.hSubMenu.IsNull || (info.fType & MENU_ITEM_TYPE.MFT_OWNERDRAW) != 0)
					return false;

				nativeItems.Add((info.wID, text, info.fType, info.fState));
			}

			// Remove handlers from the previous menu contents before rebuilding.
			foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
				item.Click -= ModernSystemMenuItem_Click;

			menu.Items.Clear();

			foreach (var nativeItem in nativeItems)
			{
				if ((nativeItem.Type & MENU_ITEM_TYPE.MFT_SEPARATOR) != 0)
				{
					menu.Items.Add(new MenuFlyoutSeparator());
					continue;
				}

				MenuFlyoutItem item;

				if (_standardSystemMenuItems.TryGetValue(nativeItem.Command, out MenuFlyoutItem? standardItem))
				{
					item = standardItem;
				}
				else
				{
					// Application-defined system commands use the same visual style.
					item = new MenuFlyoutItem
					{
						Style = _standardSystemMenuItems[StandardCommands[0]].Style
					};
				}

				item.Text = NormalizeMenuItemText(nativeItem.Text);
				item.IsEnabled = (nativeItem.State & MENU_ITEM_STATE.MFS_DISABLED) == 0;
				item.Tag = nativeItem.Command;
				item.Click += ModernSystemMenuItem_Click;

				menu.Items.Add(item);
			}

			return true;
		}

		private void ModernSystemMenuItem_Click(object sender, RoutedEventArgs args)
		{
			if (sender is not MenuFlyoutItem { Tag: uint command })
				return;

			// Native Move and Size commands enter modal loops. Close the XAML
			// flyout before dispatching the system command.
			_modernSystemMenu?.Hide();

			PInvoke.PostMessage(new HWND(WindowHandle), PInvoke.WM_SYSCOMMAND, (WPARAM)command, default);
		}

		private void ReleaseModernSystemMenu()
		{
			if (_modernSystemMenu is not null)
			{
				foreach (var item in _modernSystemMenu.Items.OfType<MenuFlyoutItem>())
					item.Click -= ModernSystemMenuItem_Click;

				_modernSystemMenu.Items.Clear();
			}

			foreach (var item in _standardSystemMenuItems.Values)
				item.Click -= ModernSystemMenuItem_Click;

			_standardSystemMenuItems.Clear();
			_modernSystemMenu = null;
			_modernSystemMenuResources = null;
		}

		private static bool TryReadNativeMenuItem(
			HMENU menu,
			uint position,
			out MENUITEMINFOW info,
			out string text)
		{
			const int BUFFER_LENGTH = 512;

			char* buffer = stackalloc char[BUFFER_LENGTH];

			// Use a local structure rather than taking the address of an out
			// parameter, which is not directly permitted in this context.
			MENUITEMINFOW nativeInfo = new()
			{
				cbSize = (uint)sizeof(MENUITEMINFOW),
				fMask = MENU_ITEM_MASK.MIIM_FTYPE |
					MENU_ITEM_MASK.MIIM_STATE |
					MENU_ITEM_MASK.MIIM_ID |
					MENU_ITEM_MASK.MIIM_SUBMENU |
					MENU_ITEM_MASK.MIIM_STRING,
				dwTypeData = buffer,
				cch = BUFFER_LENGTH - 1
			};

			if (!PInvoke.GetMenuItemInfo(menu, position, true, &nativeInfo))
			{
				info = default;
				text = string.Empty;
				return false;
			}

			int length = (int)Math.Min(nativeInfo.cch, (uint)(BUFFER_LENGTH - 1));
			text = length > 0 ? new string(buffer, 0, length) : string.Empty;

			// Never return a pointer to stack-allocated memory.
			nativeInfo.dwTypeData = null;
			info = nativeInfo;

			return true;
		}

		private static string NormalizeMenuItemText(string text)
		{
			// Win32 ampersands designate access keys. A doubled ampersand
			// represents a literal '&'.
			text = text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");

			int acceleratorIndex = text.IndexOf('\t');
			return acceleratorIndex >= 0 ? text[..acceleratorIndex] : text;
		}
	}
}

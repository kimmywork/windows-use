using System.Runtime.InteropServices;

namespace WinUse.Platform.Windows.Uia;

// ---- Property IDs (UIAutomationClient.h) ----
internal static class UIA
{
    public const int BoundingRectanglePropertyId = 30001;
    public const int ProcessIdPropertyId = 30002;
    public const int ControlTypePropertyId = 30003;
    public const int NamePropertyId = 30005;
    public const int AcceleratorKeyPropertyId = 30091;
    public const int AccessKeyPropertyId = 30006;
    public const int NativeWindowHandlePropertyId = 30020;
    public const int IsEnabledPropertyId = 30079;
    public const int IsOffscreenPropertyId = 30022;
    public const int AutomationIdPropertyId = 30011;
    public const int ClassNamePropertyId = 30012;
    public const int FrameworkIdPropertyId = 30024;
    public const int HasKeyboardFocusPropertyId = 30040;
    public const int IsKeyboardFocusablePropertyId = 30048;
    public const int ValueValuePropertyId = 30045;
    public const int IsPasswordPropertyId = 30019;

    // Pattern IDs
    public const int InvokePatternId = 10000;
    public const int SelectionItemPatternId = 10010;
    public const int ValuePatternId = 10002;
    public const int RangeValuePatternId = 10003;
    public const int ScrollPatternId = 10004;
    public const int ScrollItemPatternId = 10005;
    public const int ExpandCollapsePatternId = 10006;
    public const int GridPatternId = 10008;
    public const int GridItemPatternId = 10007;
    public const int TogglePatternId = 10015;
    public const int TextPatternId = 10014;
    public const int WindowPatternId = 10009;
    public const int LegacyIAccessiblePatternId = 10018;

    // Control type IDs
    public const int ButtonControlTypeId = 50000;
    public const int CheckBoxControlTypeId = 50002;
    public const int ComboBoxControlTypeId = 50003;
    public const int EditControlTypeId = 50004;
    public const int HyperlinkControlTypeId = 50005;
    public const int ImageControlTypeId = 50006;
    public const int ListItemControlTypeId = 50007;
    public const int ListControlTypeId = 50008;
    public const int MenuControlTypeId = 50009;
    public const int MenuBarControlTypeId = 50010;
    public const int MenuItemControlTypeId = 50011;
    public const int ProgressBarControlTypeId = 50012;
    public const int RadioButtonControlTypeId = 50013;
    public const int ScrollBarControlTypeId = 50014;
    public const int SliderControlTypeId = 50015;
    public const int SpinnerControlTypeId = 50016;
    public const int StatusBarControlTypeId = 50017;
    public const int TabControlTypeId = 50018;
    public const int TabItemControlTypeId = 50019;
    public const int TextControlTypeId = 50020;
    public const int ToolBarControlTypeId = 50021;
    public const int ToolTipControlTypeId = 50022;
    public const int TreeControlTypeId = 50023;
    public const int TreeItemControlTypeId = 50024;
    public const int CustomControlTypeId = 50025;
    public const int GroupControlTypeId = 50026;
    public const int ThumbControlTypeId = 50027;
    public const int DataGridControlTypeId = 50028;
    public const int DataItemControlTypeId = 50029;
    public const int DocumentControlTypeId = 50030;
    public const int SplitButtonControlTypeId = 50031;
    public const int WindowControlTypeId = 50032;
    public const int PaneControlTypeId = 50033;
    public const int HeaderControlTypeId = 50034;
    public const int HeaderItemControlTypeId = 50035;
    public const int TableControlTypeId = 50036;
    public const int TitleBarControlTypeId = 50037;
    public const int SeparatorControlTypeId = 50038;

    // TreeScope
    public const int ScopeElement = 1;
    public const int ScopeChildren = 2;
    public const int ScopeDescendants = 4;
    public const int ScopeSubtree = 7;

    // WindowVisualState
    public const int WindowVisualStateNormal = 0;
    public const int WindowVisualStateMaximized = 1;
    public const int WindowVisualStateMinimized = 2;
}

[ComImport]
[Guid("e22ad333-b25f-460c-83d8-29994c139781")]
internal sealed class CUIAutomation8;

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
internal interface IUIAutomation
{
    // Slots 1-26 are the original IUIAutomation members.
    [PreserveSig] int CompareElements(IUIAutomationElement a, IUIAutomationElement b, out int same);                       // 1
    [PreserveSig] int CompareRuntimeIds(IntPtr a, IntPtr b, out int same);                                                  // 2
    [PreserveSig] int GetRootElement(out IUIAutomationElement element);                                                     // 3
    [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);                                    // 4
    [PreserveSig] int ElementFromPoint(tagPOINT pt, out IUIAutomationElement element);                                      // 5
    [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);                                                  // 6
    [PreserveSig] int GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);                      // 7
    [PreserveSig] int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement element);      // 8
    [PreserveSig] int ElementFromPointBuildCache(tagPOINT pt, IntPtr cacheRequest, out IUIAutomationElement element);       // 9
    [PreserveSig] int GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);                   // 10
    [PreserveSig] int CreateTreeWalker(IUIAutomationCondition condition, out IntPtr walker);                                // 11
    [PreserveSig] int get_ControlViewWalker(out IntPtr walker);                                                             // 12
    [PreserveSig] int get_ContentViewWalker(out IntPtr walker);                                                             // 13
    [PreserveSig] int get_RawViewWalker(out IntPtr walker);                                                                 // 14
    [PreserveSig] int get_RawViewCondition(out IUIAutomationCondition condition);                                           // 15
    [PreserveSig] int get_ContentViewCondition(out IUIAutomationCondition condition);                                       // 16
    [PreserveSig] int get_ControlViewCondition(out IUIAutomationCondition condition);                                       // 17
    [PreserveSig] int CreateCacheRequest(out IntPtr cacheRequest);                                                          // 18
    [PreserveSig] int CreateTrueCondition(out IUIAutomationCondition condition);                                            // 19
    [PreserveSig] int CreateFalseCondition(out IUIAutomationCondition condition);                                           // 20
    [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value,
        out IUIAutomationCondition condition);                                                                              // 21
    [PreserveSig] int CreatePropertyConditionEx(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, int flags,
        out IUIAutomationCondition condition);                                                                              // 22
    [PreserveSig] int CreateAndCondition(IUIAutomationCondition c1, IUIAutomationCondition c2,
        out IUIAutomationCondition condition);                                                                              // 23
    [PreserveSig] int CreateAndConditionFromArray(IntPtr conditions, out IUIAutomationCondition condition);                 // 24
    [PreserveSig] int CreateOrCondition(IUIAutomationCondition c1, IUIAutomationCondition c2,
        out IUIAutomationCondition condition);                                                                              // 25
    [PreserveSig] int CreateOrConditionFromArray(IntPtr conditions, out IUIAutomationCondition condition);                 // 26

    // Remaining slots are never used; padding keeps the vtable complete.
    void Padding27(); void Padding28(); void Padding29(); void Padding30();
    void Padding31(); void Padding32(); void Padding33(); void Padding34();
    void Padding35(); void Padding36(); void Padding37(); void Padding38();
    void Padding39(); void Padding40(); void Padding41(); void Padding42();
    void Padding43(); void Padding44(); void Padding45(); void Padding46();
    void Padding47(); void Padding48(); void Padding49(); void Padding50();
    void Padding51(); void Padding52(); void Padding53(); void Padding54();
    void Padding55(); void Padding56();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("d6dd6879-b185-4479-a1aa-d010557992f0")]
internal interface IUIAutomationElement
{
    [PreserveSig] int SetFocus();                                                                                          // 1
    [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);                                                                  // 2
    [PreserveSig] int FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement? element);          // 3
    [PreserveSig] int FindAll(int scope, IUIAutomationCondition condition, out IntPtr elements);                          // 4
    [PreserveSig] int FindFirstBuildCache(int scope, IUIAutomationCondition condition, IntPtr cache,
        out IUIAutomationElement? element);                                                                                // 5
    [PreserveSig] int FindAllBuildCache(int scope, IUIAutomationCondition condition, IntPtr cache, out IntPtr elements);   // 6
    [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement element);                            // 7
    [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);         // 8
    [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue,
        [MarshalAs(UnmanagedType.Struct)] out object value);                                                               // 9
    [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);          // 10
    [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue,
        [MarshalAs(UnmanagedType.Struct)] out object value);                                                               // 11
    [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr pattern);                               // 12
    [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr pattern);                                // 13
    [PreserveSig] int GetCurrentPattern(int patternId, out IntPtr pattern);                                                // 14
    [PreserveSig] int GetCachedPattern(int patternId, out IntPtr pattern);                                                 // 15
    [PreserveSig] int GetCachedParent(out IUIAutomationElement element);                                                   // 16
    [PreserveSig] int GetCachedChildren(out IntPtr children);                                                              // 17

    // Slots 18+ are typed property accessors; we read everything via slot 8, so pad.
    void Padding18(); void Padding19(); void Padding20(); void Padding21();
    void Padding22(); void Padding23(); void Padding24(); void Padding25();
    void Padding26(); void Padding27(); void Padding28(); void Padding29();
    void Padding30(); void Padding31(); void Padding32(); void Padding33();
    void Padding34(); void Padding35(); void Padding36(); void Padding37();
    void Padding38(); void Padding39(); void Padding40(); void Padding41();
    void Padding42(); void Padding43(); void Padding44(); void Padding45();
    void Padding46(); void Padding47(); void Padding48(); void Padding49();
    void Padding50(); void Padding51(); void Padding52(); void Padding53();
    void Padding54(); void Padding55(); void Padding56(); void Padding57();
    void Padding58(); void Padding59(); void Padding60(); void Padding61();
    void Padding62(); void Padding63(); void Padding64(); void Padding65();
    void Padding66(); void Padding67(); void Padding68(); void Padding69();
    void Padding70(); void Padding71(); void Padding72(); void Padding73();
    void Padding74(); void Padding75(); void Padding76(); void Padding77();
    void Padding78(); void Padding79(); void Padding80(); void Padding81();
    void Padding82(); void Padding83(); void Padding84(); void Padding85();
    void Padding86(); void Padding87(); void Padding88(); void Padding89();
    void Padding90(); void Padding91(); void Padding92(); void Padding93();
    void Padding94(); void Padding95(); void Padding96(); void Padding97();
    void Padding98(); void Padding99(); void Padding100();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("35ffb944-6299-429b-965d-169f4b99b601")]
internal interface IUIAutomationCondition
{
    // Marker interface; no members beyond IUnknown.
}

// ---- Patterns ----

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("fb377fbf-7aa5-443a-996d-ee7b81d09194")]
internal interface IUIAutomationInvokePattern
{
    [PreserveSig] int Invoke();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("a94cd8c8-c197-49c7-8e8f-4bdc546608f1")]
internal interface IUIAutomationValuePattern
{
    [PreserveSig] int SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);   // 1
    [PreserveSig] int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string value); // 2
    [PreserveSig] int get_CurrentIsReadOnly(out int readOnly);                     // 3
    void Padding4(); void Padding5();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("94cf8356-c465-4aba-8894-c933278a4d7a")]
internal interface IUIAutomationTogglePattern
{
    [PreserveSig] int get_CurrentToggleState(out int state);   // 1
    [PreserveSig] int Toggle();                                // 2
    void Padding3();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("619e08ed-9a3b-43b2-8b72-99c38629e04e")]
internal interface IUIAutomationExpandCollapsePattern
{
    [PreserveSig] int Expand();                                  // 1
    [PreserveSig] int Collapse();                                // 2
    [PreserveSig] int get_CurrentExpandCollapseState(out int s); // 3
    void Padding4();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("b17d61f1-83cc-4af4-9061-90203487a37a")]
internal interface IUIAutomationScrollPattern
{
    [PreserveSig] int Scroll(int horizontalAmount, int verticalAmount); // 1
    [PreserveSig] int SetScrollPercent(double horizontalPercent, double verticalPercent); // 2
    void Padding3(); void Padding4(); void Padding5(); void Padding6();
    void Padding7(); void Padding8(); void Padding9(); void Padding10();
    void Padding11(); void Padding12(); void Padding13(); void Padding14();
    void Padding15(); void Padding16(); void Padding17(); void Padding18();
    void Padding19(); void Padding20();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("b4883893-6040-43e9-bc35-488d0219e39f")]
internal interface IUIAutomationScrollItemPattern
{
    [PreserveSig] int ScrollIntoView();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("78f8ef51-4b0c-4c44-90b2-089a0e6cd398")]
internal interface IUIAutomationSelectionItemPattern
{
    [PreserveSig] int Select();                                     // 1
    [PreserveSig] int AddToSelection();                             // 2
    [PreserveSig] int RemoveFromSelection();                        // 3
    [PreserveSig] int get_CurrentIsSelected(out int selected);      // 4
    void Padding5();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("987df77b-db06-4d77-8f8a-86a319f78396")]
internal interface IUIAutomationWindowPattern
{
    [PreserveSig] int SetWindowVisualState(int state);   // 1
    [PreserveSig] int WaitForInputIdle(int ms, out int result); // 2
    [PreserveSig] int Close();                           // 3
    void Padding4(); void Padding5(); void Padding6(); void Padding7();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("32eba293-9c70-4c10-b47a-977a8290611f")]
internal interface IUIAutomationTextPattern
{
    void Padding1(); void Padding2(); void Padding3(); void Padding4();
    [PreserveSig] int get_DocumentRange(out IUIAutomationTextRange range); // 5
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("a543c4cc-f42e-4844-9c86-1755202625d6")]
internal interface IUIAutomationTextRange
{
    void Padding1(); void Padding2(); void Padding3();
    [PreserveSig] int ExpandToEnclosingUnit(int textUnit); // 4
    void Padding5(); void Padding6(); void Padding7(); void Padding8();
    void Padding9(); void Padding10(); void Padding11(); void Padding12();
    [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text); // 13
    void Padding14(); void Padding15(); void Padding16(); void Padding17();
}

[StructLayout(LayoutKind.Sequential)]
internal struct tagPOINT
{
    public int X;
    public int Y;
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("1431459b-6bc1-44ee-9ca1-6e87ef6b3013")]
internal interface IUIAutomationElementArray
{
    [PreserveSig] int get_Length(out int length);                                    // 1
    [PreserveSig] int GetElement(int index, out IUIAutomationElement element);      // 2
}

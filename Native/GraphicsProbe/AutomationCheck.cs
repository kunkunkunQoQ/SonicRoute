using System.Runtime.InteropServices;
using SonicRoute.Native.Graphics;

namespace SonicRoute.GraphicsProbe;

internal static unsafe class AutomationCheck
{
    private static readonly Guid CUIAutomation=new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IUIAutomation=new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
    private static readonly Guid ValuePattern=new("a94cd8b1-0844-4cd6-9d2d-640537ab39e9");
    private static readonly Guid RangePattern=new("59213f4f-7346-49e5-b120-80555987a148");
    [DllImport("ole32")] private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32")] private static extern void CoUninitialize();
    [DllImport("ole32")] private static extern int CoCreateInstance(in Guid cls,nint outer,uint context,in Guid iid,out nint value);
    [DllImport("oleaut32")] private static extern void SysFreeString(nint text);

    internal static (string Text,double Value,int EditType,int SliderType) Read(nint desktop,nint edit,nint slider)
    {
        if(desktop!=0) Win.Ok(Win.SetThreadDesktop(desktop),"UIA thread desktop");
        NativeCom.Check(CoInitializeEx(0,0));
        nint automation=0,editElement=0,sliderElement=0,editPattern=0,sliderPattern=0,str=0;
        try
        {
            NativeCom.Check(CoCreateInstance(in CUIAutomation,0,1,in IUIAutomation,out automation));
            int Element(nint hwnd,nint* p) =>
                ((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)NativeCom.Slot(automation,6))(automation,hwnd,p);
            NativeCom.Check(Element(edit,&editElement));
            NativeCom.Check(Element(slider,&sliderElement));
            int editType=ControlType(editElement),sliderType=ControlType(sliderElement);
            editPattern=Pattern(editElement,10002,ValuePattern);
            sliderPattern=Pattern(sliderElement,10003,RangePattern);
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)NativeCom.Slot(editPattern,4))(editPattern,&str));
            double v=0;
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint,double*,int>)NativeCom.Slot(sliderPattern,4))(sliderPattern,&v));
            return (Marshal.PtrToStringBSTR(str),v,editType,sliderType);
        }
        finally
        {
            if(str!=0) SysFreeString(str);
            NativeCom.Release(ref sliderPattern);NativeCom.Release(ref editPattern);
            NativeCom.Release(ref sliderElement);NativeCom.Release(ref editElement);
            NativeCom.Release(ref automation);CoUninitialize();
        }
    }
    private static int ControlType(nint element)
    {
        int type=0;NativeCom.Check(((delegate* unmanaged[Stdcall]<nint,int*,int>)NativeCom.Slot(element,21))(element,&type));
        return type;
    }
    private static nint Pattern(nint element,int patternId,Guid iid)
    {
        nint unknown=0,typed=0;
        try
        {
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint,int,nint*,int>)NativeCom.Slot(element,16))(element,patternId,&unknown));
            if(unknown==0) throw new InvalidOperationException($"UIA pattern {patternId} unavailable.");
            NativeCom.Check(((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)NativeCom.Slot(unknown,0))(unknown,&iid,&typed));
            return typed;
        }
        finally { NativeCom.Release(ref unknown); }
    }
}

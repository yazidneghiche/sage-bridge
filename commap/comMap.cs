using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

[ComImport, Guid("00020400-0000-0000-C000-000000000046"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDispatch
{
    int GetTypeInfoCount();
    [PreserveSig] int GetTypeInfo(int iTInfo, int lcid, out ITypeInfo typeInfo);
    void GetIDsOfNames(); // unused
    void Invoke();        // unused
}

class ComTypeInfoDump
{
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        object stream = null;
        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            Console.WriteLine("OK: Stream instance created.");
            DumpTypeInfo("stream", stream);

            object fdv = null;
            try { fdv = stream.GetType().InvokeMember("FactoryDocumentVente", System.Reflection.BindingFlags.GetProperty, null, stream, null); }
            catch { fdv = null; }

            if (fdv != null)
            {
                Console.WriteLine();
                Console.WriteLine("OK: FactoryDocumentVente property read.");
                DumpTypeInfo("FactoryDocumentVente", fdv);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("KO: FactoryDocumentVente not accessible.");
            }

            object fa = null;
            try { fa = stream.GetType().InvokeMember("FactoryArticle", System.Reflection.BindingFlags.GetProperty, null, stream, null); }
            catch { fa = null; }

            if (fa != null)
            {
                Console.WriteLine();
                Console.WriteLine("OK: FactoryArticle property read.");
                DumpTypeInfo("FactoryArticle", fa);
            }

            Console.WriteLine();
            Console.WriteLine("DONE");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
        }
        finally
        {
            try { if (stream != null) Marshal.FinalReleaseComObject(stream); } catch { }
        }
    }

    static void DumpTypeInfo(string label, object comObj)
    {
        Console.WriteLine("==== " + label + " (IDispatch TypeInfo) ====");

        IDispatch disp = null;
        try { disp = (IDispatch)comObj; } catch { disp = null; }

        if (disp == null)
        {
            Console.WriteLine("Not IDispatch / cannot cast.");
            return;
        }

        ITypeInfo ti;
        int hr = disp.GetTypeInfo(0, 0, out ti);
        if (hr != 0 || ti == null)
        {
            Console.WriteLine("GetTypeInfo failed. hr=" + hr);
            return;
        }

        IntPtr pAttr;
        ti.GetTypeAttr(out pAttr);
        TYPEATTR attr = (TYPEATTR)Marshal.PtrToStructure(pAttr, typeof(TYPEATTR));

        Console.WriteLine("Funcs=" + attr.cFuncs + " Vars=" + attr.cVars);

        for (int i = 0; i < attr.cFuncs; i++)
        {
            IntPtr pFuncDesc;
            ti.GetFuncDesc(i, out pFuncDesc);
            FUNCDESC fd = (FUNCDESC)Marshal.PtrToStructure(pFuncDesc, typeof(FUNCDESC));

            string[] names = new string[fd.cParams + 1];
            int cNames;
            ti.GetNames(fd.memid, names, names.Length, out cNames);

            string name = (cNames > 0 && names[0] != null) ? names[0] : ("memid_" + fd.memid);
            Console.WriteLine("- " + name + " (params=" + fd.cParams + ", invkind=" + fd.invkind + ")");

            ti.ReleaseFuncDesc(pFuncDesc);
        }

        ti.ReleaseTypeAttr(pAttr);
    }
}
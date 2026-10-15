using System;
using System.IO;
using System.Text;

class ComMap
{
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        dynamic stream = null;
        try
        {
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);

            Console.WriteLine("Stream type: " + stream.GetType().FullName);

            DumpTypeInfo("stream", stream);

            // Essaie d’attraper FactoryDocumentVente (si existe)
            dynamic fdv = null;
            try { fdv = stream.FactoryDocumentVente; } catch { }
            if (fdv != null) DumpTypeInfo("FactoryDocumentVente", fdv);
            else Console.WriteLine("FactoryDocumentVente: introuvable sur stream");

            // Essaie FactoryArticle
            dynamic fa = null;
            try { fa = stream.FactoryArticle; } catch { }
            if (fa != null) DumpTypeInfo("FactoryArticle", fa);

            // Essaie CptaApplication.FactoryClient (vu que chez toi ça marche)
            dynamic fc = null;
            try { fc = stream.CptaApplication.FactoryClient; } catch { }
            if (fc != null) DumpTypeInfo("CptaApplication.FactoryClient", fc);

            Console.WriteLine("\nOK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex);
        }
        finally
        {
            try { if (stream != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream); } catch { }
        }
    }

    static void DumpTypeInfo(string name, object comObj)
    {
        Console.WriteLine("\n==== " + name + " ====");
        try
        {
            // TypeName COM (VB style)
            Console.WriteLine("TypeName: " + Microsoft.VisualBasic.Information.TypeName(comObj));
        }
        catch { }

        try
        {
            // Liste des membres COM exposés
            var t = comObj.GetType();
            var members = t.GetMembers();
            Console.WriteLine("Members count: " + members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                Console.WriteLine(members[i].MemberType + " " + members[i].Name);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Dump failed: " + ex.Message);
        }
    }
}
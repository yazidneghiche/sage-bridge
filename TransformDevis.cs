using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

public class SageConfig
{
    public string GcmPath { get; set; }
    public string SageUser { get; set; }
    public string SagePassword { get; set; }
}

class TransformDevis
{
    // Usage : TransformDevis.exe chemin\config.json DE157709
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 2)
        {
            Console.WriteLine("Usage : TransformDevis.exe chemin\\config.json numeroDevis");
            return 1;
        }

        dynamic stream = null;
        dynamic devis = null;
        dynamic process = null;
        bool transformationLancee = false;
        try
        {
            var cfg = new JavaScriptSerializer().Deserialize<SageConfig>(
                File.ReadAllText(args[0], Encoding.UTF8));
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.GcmPath))
                throw new Exception("Configuration Sage invalide.");

            stream = Activator.CreateInstance(Type.GetTypeFromProgID("Objets100c.Cial.Stream", true));
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();

            devis = stream.FactoryDocumentVente.ReadPiece(0, args[1]);
            Console.WriteLine("Devis : " + devis.DO_Piece
                + " | lignes : " + devis.FactoryDocumentLigne.List.Count);

            // Meme preparation que le bridge pour un devis destine a devenir une commande.
            Console.WriteLine("Statut avant preparation : " + devis.DO_Statut);
            devis.DO_Statut = 2;
            devis.Write();
            Console.WriteLine("Statut enregistre : " + devis.DO_Statut);

            // Un seul processus pour le devis entier, sans ajout de lignes ni création directe de BC.
            process = stream.Transformation.Vente.CreateProcess_Commander();
            process.AddDocument(devis);
            process.DO_Date = DateTime.Today;
            if (!process.CanProcess)
            {
                for (int i = 1; i <= process.Errors.Count; i++)
                    Console.WriteLine(process.Errors.Item(i).Text);
                throw new Exception("Sage refuse la transformation.");
            }

            transformationLancee = true;
            process.Process();

            // Afficher tous les résultats, même si Sage sépare les articles.
            int count = process.ListDocumentsResult.Count;
            Console.WriteLine("Documents produits : " + count);
            for (int i = 1; i <= count; i++)
            {
                dynamic commande = process.ListDocumentsResult(i);
                try
                {
                    Console.WriteLine("Piece : " + commande.DO_Piece
                        + " | type : " + commande.DO_Type
                        + " | lignes : " + commande.FactoryDocumentLigne.List.Count);
                }
                finally { Release(commande); }
            }
            if (count != 1)
            {
                Console.WriteLine("Résultat inattendu : contrôler les documents dans Sage avant toute relance.");
                return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            if (transformationLancee)
                Console.Error.WriteLine("La transformation a été lancée : vérifier Sage avant toute relance.");
            return 1;
        }
        finally
        {
            Release(process);
            Release(devis);
            try { if (stream != null) stream.Close(); } catch { }
            Release(stream);
        }
    }

    static void Release(object value)
    {
        try
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch { }
    }
}

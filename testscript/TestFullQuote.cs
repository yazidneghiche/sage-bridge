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

class TestFullQuote
{
    // Test isole : devis avec deux articles, puis transformation globale uniquement.
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 1)
        {
            Console.WriteLine("Usage : TestFullQuote.exe chemin\\config.json");
            return 1;
        }

        dynamic stream = null;
        dynamic devis = null;
        dynamic process = null;
        dynamic client = null;
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

            client = stream.CptaApplication.FactoryClient.ReadNumero("TEST");
            devis = stream.FactoryDocumentVente.CreateType(0);
            devis.SetDefaultClient(client);
            devis.DO_Date = DateTime.Today;
            devis.DO_Statut = 2;
            devis.DO_Ref = "TEST-PLEIN-" + DateTime.Now.ToString("HHmmss");
            devis.DO_RefExterne = "API-TEST-PLEIN-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            devis.Write();
            string quotePiece = devis.DO_Piece;
            Console.WriteLine("Devis cree : " + quotePiece);
            AddArticle(devis, stream, "10017-1", 2);
            AddArticle(devis, stream, "10102-3", 1);

            // Relire le devis enregistre avant de transformer le document entier.
            Release(devis); devis = null;
            devis = stream.FactoryDocumentVente.ReadPiece(0, quotePiece);
            Console.WriteLine("Devis relu : " + devis.DO_Piece
                + " | lignes : " + devis.FactoryDocumentLigne.List.Count
                + " | statut : " + devis.DO_Statut);

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

            process.Process();

            // Afficher tous les résultats, même si Sage sépare les articles.
            int count = process.ListDocumentsResult.Count;
            Console.WriteLine("Documents produits : " + count);
            for (int i = 1; i <= count; i++)
            {
                dynamic commande = null;
                try
                {
                    commande = process.ListDocumentsResult(i);
                    Console.WriteLine("Piece : " + commande.DO_Piece
                        + " | type COM : " + commande.DO_Type
                        + " | lignes : " + commande.FactoryDocumentLigne.List.Count);
                }
                finally { Release(commande); }
            }
            // Aucun ajout ni modification des commandes apres transformation.
            return count == 1 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            Console.Error.WriteLine("Verifier les pieces journalisees avant toute relance : aucun retour arriere automatique.");
            return 1;
        }
        finally
        {
            Release(process);
            Release(devis);
            Release(client);
            try { if (stream != null) stream.Close(); } catch { }
            Release(stream);
        }
    }

    static void AddArticle(dynamic order, dynamic stream, string reference, double quantity)
    {
        dynamic article = null, line = null;
        try
        {
            article = stream.FactoryArticle.ReadReference(reference);
            line = order.FactoryDocumentLigne.Create();
            line.SetDefaultArticle(article, quantity);
            Console.WriteLine("Ajout en cours : " + reference + " | quantite : " + quantity);
            line.Write();
            Console.WriteLine("Ajout enregistre : " + reference);
        }
        finally { Release(line); Release(article); }
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

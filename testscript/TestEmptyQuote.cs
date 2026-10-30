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

class TestEmptyQuote
{
    // Test isole : devis vide, transformation, puis ajout de deux articles.
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 1)
        {
            Console.WriteLine("Usage : TestEmptyQuote.exe chemin\\config.json");
            return 1;
        }

        dynamic stream = null;
        dynamic devis = null;
        dynamic process = null;
        dynamic destination = null;
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
            devis.DO_Ref = "TEST-VIDE-" + DateTime.Now.ToString("HHmmss");
            devis.DO_RefExterne = "API-TEST-VIDE-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            devis.Write();
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

            process.Process();

            // Afficher tous les résultats, même si Sage sépare les articles.
            int count = process.ListDocumentsResult.Count;
            Console.WriteLine("Documents produits : " + count);
            if (count != 1)
            {
                Console.WriteLine("Le test s'arrete : une seule commande etait attendue. Aucun article ajoute.");
                for (int i = 1; i <= count; i++)
                    Console.WriteLine("Piece produite : " + process.ListDocumentsResult(i).DO_Piece);
                return 2;
            }
            // Utiliser directement la commande retournee par la transformation.
            destination = process.ListDocumentsResult(1);
            Console.WriteLine("Commande obtenue : " + destination.DO_Piece
                + " | type COM : " + destination.DO_Type);
            AddArticle(destination, stream, "10017-1", 2);
            AddArticle(destination, stream, "10102-3", 1);
            string piece = destination.DO_Piece;
            object typeCom = destination.DO_Type;
            Release(destination); destination = null;
            destination = stream.FactoryDocumentVente.ReadPiece(typeCom, piece);
            Console.WriteLine("Commande relue : " + destination.DO_Piece
                + " | lignes : " + destination.FactoryDocumentLigne.List.Count);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            Console.Error.WriteLine("Verifier les pieces journalisees avant toute relance : aucun retour arriere automatique.");
            return 1;
        }
        finally
        {
            Release(destination);
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

using System;
using System.IO;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

public class AddProductConfig
{
    public string GcmPath { get; set; }
    public string SageUser { get; set; }
    public string SagePassword { get; set; }
}

class AddProductToOrder
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 4)
        {
            Console.WriteLine("Usage : AddProductToOrder.exe config.json BC159602 referenceArticle quantite");
            return 1;
        }
        dynamic stream = null, order = null, article = null, line = null;
        bool writeStarted = false;
        try
        {
            decimal quantity;
            if (!decimal.TryParse(args[3].Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out quantity) || quantity <= 0)
                throw new Exception("Quantite positive obligatoire.");
            var cfg = new JavaScriptSerializer().Deserialize<AddProductConfig>(
                File.ReadAllText(args[0], Encoding.UTF8));
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.GcmPath))
                throw new Exception("Configuration Sage invalide.");
            stream = Activator.CreateInstance(Type.GetTypeFromProgID("Objets100c.Cial.Stream", true));
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();

            // Valeur COM observee sur les commandes retournees par cette installation : 10.
            // Le DO_Type SQL = 1 ne doit pas etre utilise ici.
            const int CommandeTypeCom = 10;
            order = stream.FactoryDocumentVente.ReadPiece(CommandeTypeCom, args[1]);
            if (Convert.ToInt32(order.DO_Type) != CommandeTypeCom)
                throw new Exception("Le document lu n'est pas du type commande COM attendu.");
            string piece = order.DO_Piece;
            int before = order.FactoryDocumentLigne.List.Count;
            Console.WriteLine("Commande : " + piece
                + " | type COM : " + order.DO_Type + " | lignes avant : " + before);
            article = stream.FactoryArticle.ReadReference(args[2]);
            line = order.FactoryDocumentLigne.Create();
            // Les tarifs et conditions sont initialises par Sage pour cette commande.
            line.SetDefaultArticle(article, (double)quantity);
            writeStarted = true;
            line.Write();
            Console.WriteLine("Article ajoute : " + args[2] + " | quantite : " + quantity
                + " | ligne : " + line.DL_Ligne + " | PU HT Sage : " + line.DL_PrixUnitaire);
            Release(line); line = null;
            Release(order); order = null;
            order = stream.FactoryDocumentVente.ReadPiece(CommandeTypeCom, piece);
            Console.WriteLine("Commande relue : " + order.DO_Piece
                + " | lignes apres : " + order.FactoryDocumentLigne.List.Count);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            if (writeStarted)
                Console.Error.WriteLine("Ecriture tentee : verifier les lignes avant de relancer pour eviter un double ajout.");
            return 1;
        }
        finally
        {
            Release(line); Release(article); Release(order);
            try { if (stream != null) stream.Close(); } catch { }
            Release(stream);
        }
    }
    static void Release(object value)
    {
        try
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch { }
    }
}

using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

public class PriceTestConfig
{
    public string GcmPath { get; set; }
    public string SageUser { get; set; }
    public string SagePassword { get; set; }
}
class TestOrderPrices
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length != 1) { Console.WriteLine("Usage : TestOrderPrices.exe config.json"); return 1; }
        dynamic stream = null, order = null;
        var lines = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        bool writeStarted = false;
        try
        {
            var cfg = new JavaScriptSerializer().Deserialize<PriceTestConfig>(File.ReadAllText(args[0], Encoding.UTF8));
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.GcmPath)) throw new Exception("Configuration invalide.");
            stream = Activator.CreateInstance(Type.GetTypeFromProgID("Objets100c.Cial.Stream", true));
            stream.Name = cfg.GcmPath;
            stream.Loggable.UserName = cfg.SageUser;
            stream.Loggable.UserPwd = cfg.SagePassword;
            stream.Open();
            // Commande TEST verifiee en SQL ; 10 est son type COM observe.
            const string piece = "BC159605";
            order = stream.FactoryDocumentVente.ReadPiece(10, piece);
            if ((int)order.FactoryDocumentLigne.List.Count != 2) throw new Exception("Deux lignes exactement attendues : aucune modification.");
            for (int i = 1; i <= 2; i++)
            {
                dynamic line = order.FactoryDocumentLigne.List(i);
                dynamic article = null;
                bool retained = false;
                try
                {
                    article = line.Article;
                    string reference = article.AR_Ref;
                    if (lines.ContainsKey(reference)) throw new Exception("Article repete : test annule avant ecriture.");
                    lines.Add(reference, (object)line); retained = true;
                    Console.WriteLine("Avant : " + reference + " | qte=" + line.DL_Qte + " | PU HT=" + line.DL_PrixUnitaire);
                }
                finally { Release(article); if (!retained) Release(line); }
            }
            if (!lines.ContainsKey("10017-1") || !lines.ContainsKey("10102-3")) throw new Exception("Articles inattendus : aucune modification.");
            dynamic first = lines["10017-1"], second = lines["10102-3"];
            if (Convert.ToDecimal(first.DL_Qte) != 2m || Convert.ToDecimal(second.DL_Qte) != 1m)
                throw new Exception("Quantites inattendues : aucune modification.");

            // Synchroniser les prix HT, TTC et devise suivant l'automatisme documente Sage.
            stream.FactoryDocumentLigne.AutoSet_PrixLigne = true;
            writeStarted = true;
            first.DL_PrixUnitaire = 10.50;
            first.Write();
            Console.WriteLine("Prix enregistre : 10017-1 = 10.50 HT");
            second.DL_PrixUnitaire = 25.00;
            second.Write();
            Console.WriteLine("Prix enregistre : 10102-3 = 25.00 HT");
            order.RecalculTotaux(true);
            foreach (object value in lines.Values) Release(value);
            lines.Clear(); first = null; second = null;
            Release(order); order = null;
            order = stream.FactoryDocumentVente.ReadPiece(10, piece);
            bool pricesCorrect = true;
            for (int i = 1; i <= order.FactoryDocumentLigne.List.Count; i++)
            {
                dynamic line = null, article = null;
                try
                {
                    line = order.FactoryDocumentLigne.List(i);
                    article = line.Article;
                    string reference = article.AR_Ref;
                    decimal expected = reference == "10017-1" ? 10.50m : 25m;
                    decimal actual = Convert.ToDecimal(line.DL_PrixUnitaire);
                    Console.WriteLine("Apres relecture : " + reference + " | PU HT=" + actual + " | montant HT=" + line.DL_MontantHT);
                    if (Math.Abs(actual - expected) > 0.0001m) pricesCorrect = false;
                }
                finally { Release(article); Release(line); }
            }
            if (!pricesCorrect) throw new Exception("Les prix relus ne correspondent pas aux prix demandes.");
            Console.WriteLine("Prix verifies. Controle aussi les totaux en SQL.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            if (writeStarted) Console.Error.WriteLine("Ecriture tentee : verifier les prix en SQL. Aucun retour arriere automatique.");
            return 1;
        }
        finally
        {
            foreach (object value in lines.Values) Release(value);
            Release(order);
            try { if (stream != null) stream.Close(); } catch { }
            Release(stream);
        }
    }
    static void Release(object value)
    {
        try { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); } catch { }
    }
}

using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization; // dispo sur .NET Framework
using System.Collections.Generic;

public class BridgeRequest
{
    public string gcmPath { get; set; }
    public string sageUser { get; set; }
    public string sagePassword { get; set; }
    public Order order { get; set; }
}

public class Order
{
    public string customerCode { get; set; }
    public string date { get; set; } // yyyy-MM-dd
    public string reference { get; set; }
    public string warehouse { get; set; }
    public List<OrderLine> lines { get; set; }
}

public class OrderLine
{
    public string itemCode { get; set; }
    public decimal quantity { get; set; }
    public decimal? unitPrice { get; set; }
}

public class BridgeResponse
{
    public bool success { get; set; }
    public string sageDocumentNumber { get; set; }
    public string message { get; set; }
    public List<string> errors { get; set; } = new List<string>();
}

class Program
{
    static int Main(string[] args)
    {
        // Usage:
        // SageBridge.exe create-order --in "C:\...\requests\order.json" --out "C:\...\responses\order.json"
        try
        {
            var action = args.Length > 0 ? args[0] : "";
            var inPath = GetArg(args, "--in");
            var outPath = GetArg(args, "--out");

            if (action != "create-order" || string.IsNullOrWhiteSpace(inPath) || string.IsNullOrWhiteSpace(outPath))
                return Fail(outPath, "Usage: SageBridge.exe create-order --in <file.json> --out <file.json>");

            var reqJson = File.ReadAllText(inPath, Encoding.UTF8);
            var js = new JavaScriptSerializer();
            var req = js.Deserialize<BridgeRequest>(reqJson);

            var resp = CreateOrder(req);

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, js.Serialize(resp), Encoding.UTF8);

            return resp.success ? 0 : 2;
        }
        catch (Exception ex)
        {
            // Dernier filet de sécurité
            Console.Error.WriteLine(ex);
            return 99;
        }
    }

    static string GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    static int Fail(string outPath, string message)
    {
        try
        {
            var js = new JavaScriptSerializer();
            var resp = new BridgeResponse { success = false, message = message };
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllText(outPath, js.Serialize(resp), Encoding.UTF8);
            }
        }
        catch { }
        Console.Error.WriteLine(message);
        return 1;
    }

    static BridgeResponse CreateOrder(BridgeRequest req)
    {
        var resp = new BridgeResponse();

        // Validations
        if (req == null) { resp.success = false; resp.message = "JSON invalide"; return resp; }
        if (string.IsNullOrWhiteSpace(req.gcmPath) || !File.Exists(req.gcmPath))
        { resp.success = false; resp.message = "gcmPath introuvable"; return resp; }
        if (req.order == null || string.IsNullOrWhiteSpace(req.order.customerCode))
        { resp.success = false; resp.message = "order/customerCode manquant"; return resp; }
        if (req.order.lines == null || req.order.lines.Count == 0)
        { resp.success = false; resp.message = "order/lines vide"; return resp; }

        dynamic stream = null;
        try
        {
            // 1) Ouvrir la société
            var t = Type.GetTypeFromProgID("Objets100c.Cial.Stream", true);
            stream = Activator.CreateInstance(t);
            stream.Name = req.gcmPath;
            stream.Loggable.UserName = req.sageUser;
            stream.Loggable.UserPwd = req.sagePassword;
            stream.Open();

            // 2) Créer une commande client
            // ⚠️ ICI: dépend de ta version/config Sage (type doc, collection, etc.)
            // Exemples possibles: stream.Documents, stream.FactoryDocument, stream.Cial.Documents...
            dynamic docs = stream.Documents;              // À confirmer
            dynamic doc = docs.Add();                     // À confirmer

            // ⚠️ À confirmer: Type doc "commande client"
            // Souvent il existe une constante/enumeration : DocumentTypeCommandeClient
            // sinon c’est un code numérique. On le déterminera depuis Sage.
            doc.Type = /* TODO: valeur COMMANDE CLIENT */ 0;

            doc.Tiers = req.order.customerCode;
            if (!string.IsNullOrWhiteSpace(req.order.reference)) doc.Reference = req.order.reference;

            // Date
            // Selon Sage: parfois Date = "dd/MM/yyyy", parfois un DateTime COM.
            // Tentative DateTime:
            DateTime dt;
            if (DateTime.TryParse(req.order.date, out dt)) doc.Date = dt;

            // Dépôt/entrepôt si requis
            if (!string.IsNullOrWhiteSpace(req.order.warehouse))
                doc.Depot = req.order.warehouse; // à confirmer le nom de propriété

            // 3) Lignes
            foreach (var line in req.order.lines)
            {
                dynamic l = doc.Lignes.Add(); // À confirmer
                l.Article = line.itemCode;
                l.Quantite = line.quantity;
                if (line.unitPrice.HasValue) l.PrixUnitaire = line.unitPrice.Value;
            }

            // 4) Save/Validate
            doc.Write(); // ou Save()/Validate() selon version

            // 5) Récupérer numéro doc
            resp.success = true;
            resp.message = "Commande créée";
            try { resp.sageDocumentNumber = (string)doc.Numero; } catch { /* ignore */ }

            stream.Close();
            return resp;
        }
        catch (Exception ex)
        {
            resp.success = false;
            resp.message = "Erreur création commande";
            resp.errors.Add(ex.Message);
            resp.errors.Add(ex.ToString());
            try { if (stream != null) stream.Close(); } catch { }
            return resp;
        }
        finally
        {
            try
            {
                if (stream != null)
                {
                    // libérer COM
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(stream);
                }
            }
            catch { }
        }
    }
}
using System.Management.Automation;

namespace TripIt.PowerShell;

/// <summary>
/// <para type="synopsis">Gets the user's TripIt subscription / purchased product info.</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "TripItPurchasedProduct")]
[OutputType(typeof(TripIt.Models.PurchasedProductInfo))]
public sealed class GetTripItPurchasedProductCommand : TripItCmdletBase
{
    protected override void ProcessRecord()
    {
        var client = GetClient();
        var result = RunSync(client.GetPurchasedProductsAsync);
        WriteObject(result);
    }
}

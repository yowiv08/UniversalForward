using System.Text;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ResponsesResourceStateErrorTests
{
    [TestMethod]
    [DataRow("""{"error":{"code":"invalid_encrypted_content"}}""", "invalid_encrypted_content")]
    [DataRow("""{"error":{"code":"previous_response_not_found"}}""", "response_not_found")]
    [DataRow("""{"error":{"code":"item_not_found"}}""", "item_not_found")]
    [DataRow("""{"error":{"message":"The encrypted content gAAAAA could not be verified."}}""", "invalid_encrypted_content")]
    [DataRow("""{"error":{"message":"Previous response with id 'resp_old' not found."}}""", "response_not_found")]
    [DataRow("""{"error":{"message":"No response found with id 'resp_old'."}}""", "response_not_found")]
    [DataRow("""{"error":{"message":"Item with id 'rs_old' not found."}}""", "item_not_found")]
    [DataRow("""{"error":{"param":"input[1].id","message":"Not found."}}""", "item_not_found")]
    [DataRow("""{"error":{"param":"input.1.encrypted_content","message":"Invalid value."}}""", "invalid_encrypted_content")]
    [DataRow("""{"error":{"param":"input[1].encrypted_content","message":"Missing required field."}}""", "invalid_encrypted_content")]
    public void RecognizesExplicitResourceStateErrors(string body, string reason)
    {
        var analysis = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes(body), false, false);
        Assert.AreEqual(reason, ResponsesResourceState.RecoveryReason(analysis));
    }

    [TestMethod]
    [DataRow("""{"error":{"param":"input","message":"bad response status code 400"}}""")]
    [DataRow("""{"error":{"param":"reasoning.effort","message":"Invalid value."}}""")]
    [DataRow("""{"error":{"param":"input[1].arguments.id","message":"Not found."}}""")]
    [DataRow("""{"error":{"param":"input[1].arguments.encrypted_content","message":"Invalid value."}}""")]
    [DataRow("""{"error":{"code":"model_not_found","message":"The model does not exist."}}""")]
    [DataRow("""{"error":{"message":"Item 'fc_old' was provided without its required 'reasoning' item."}}""")]
    [DataRow("""{"error":{"code":"rate_limit_exceeded","message":"Too many requests with encrypted content."}}""")]
    [DataRow("""{"error":{"code":"insufficient_quota","message":"Invalid encrypted content; account quota exhausted."}}""")]
    [DataRow("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"invalid_encrypted_content"}]}]}""")]
    [DataRow("""{"type":"response.output_item.done","item":{"type":"message","error":{"code":"invalid_encrypted_content"}}}""")]
    [DataRow("""{"error":{"code":"invalid_encrypted_content"},"output":[{"type":"reasoning","encrypted_content":"new state"}]}""")]
    [DataRow("""{"error":{"code":"invalid_encrypted_content"},"output":[{"type":"function_call","arguments":""}]}""")]
    public void DoesNotInferStateFailureFromUnrelatedErrorsOrOutput(string body)
    {
        var analysis = ForwardResponseAnalysis.Read(Encoding.UTF8.GetBytes(body), false, false);
        Assert.IsNull(ResponsesResourceState.RecoveryReason(analysis));
    }
}

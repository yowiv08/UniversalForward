using System.Text.Json.Nodes;
using Plugins.UniversalForward;

namespace UniversalForward.Tests;

[TestClass]
public sealed class ResponsesResourceStateTests
{
    private static JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();

    [TestMethod]
    public void StripsResourceStateAndPreservesConversationAndToolCorrelation()
    {
        var body = Object("""
            {
              "model": "model",
              "previous_response_id": "resp_previous",
              "include": ["reasoning.encrypted_content"],
              "input": [
                {"id":"msg_1","role":"user","content":[{"type":"input_text","text":"继续处理中文消息"}]},
                {"id":"rs_1","type":"reasoning","encrypted_content":"cipher","encrypted_extra":"more","summary":[{"type":"summary_text","text":"Use the tool"}]},
                {"id":"fc_1","type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"id\":\"business-id\"}"},
                {"id":"out_1","type":"function_call_output","call_id":"call_1","output":"found"}
              ]
            }
            """);
        var expected = Object("""
            {
              "model": "model",
              "include": ["reasoning.encrypted_content"],
              "input": [
                {"role":"user","content":[{"type":"input_text","text":"继续处理中文消息"}]},
                {"type":"reasoning","summary":[{"type":"summary_text","text":"Use the tool"}]},
                {"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{\"id\":\"business-id\"}"},
                {"type":"function_call_output","call_id":"call_1","output":"found"}
              ]
            }
            """);

        Assert.AreEqual(7, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        Assert.AreEqual(0, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    public void LeavesRootAndNestedFieldsUntouched()
    {
        var body = Object("""
            {
              "id":"root-id",
              "encrypted_content":"root-cipher",
              "metadata":{"previous_response_id":"metadata-id"},
              "input":[{
                "id":"item-id",
                "type":"custom_tool_call",
                "call_id":"call_1",
                "input":{"id":"business-id","encrypted_content":"business-value","previous_response_id":"business-response"},
                "content":[{"id":"content-id","encrypted_content":"content-value"}]
              }]
            }
            """);
        var expected = body.DeepClone().AsObject();
        expected["input"]![0]!.AsObject().Remove("id");

        Assert.AreEqual(1, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    public void CountsNullFieldsAndUsesCaseSensitiveEncryptedPrefix()
    {
        var body = Object("""
            {
              "previous_response_id":null,
              "Previous_response_id":"keep",
              "input":[{
                "id":null,
                "Id":"keep",
                "encrypted":null,
                "encrypted_content":null,
                "encryptedFuture":{"value":1},
                "Encrypted_content":"keep",
                "nonencrypted":"keep"
              }]
            }
            """);
        var expected = Object("""
            {
              "Previous_response_id":"keep",
              "input":[{"Id":"keep","Encrypted_content":"keep","nonencrypted":"keep"}]
            }
            """);

        Assert.AreEqual(5, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"input\":null}")]
    [DataRow("{\"input\":\"a new message\"}")]
    [DataRow("{\"input\":{\"id\":\"object-id\",\"encrypted_content\":\"keep\"}}")]
    public void NonArrayInputRemainsUnchanged(string json)
    {
        var body = Object(json);
        var expected = body.DeepClone();

        Assert.AreEqual(0, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
        body["previous_response_id"] = "resp_previous";
        Assert.AreEqual(1, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }

    [TestMethod]
    public void IgnoresNonObjectArrayItemsAndRetainsReasoningItems()
    {
        var body = Object("""
            {"input":[null,"plain",12,[{"id":"nested-id","encrypted_content":"keep"}],{"type":"reasoning","id":"rs_1","encrypted_content":"cipher"},{}]}
            """);
        var expected = Object("""
            {"input":[null,"plain",12,[{"id":"nested-id","encrypted_content":"keep"}],{"type":"reasoning"},{}]}
            """);

        Assert.AreEqual(2, ResponsesResourceState.Strip(body));
        Assert.IsTrue(JsonNode.DeepEquals(expected, body));
    }
}

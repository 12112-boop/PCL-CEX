using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Link.Lobby;

namespace PCL.Core.Test;

[TestClass]
public class LobbyCodeTerracottaTest
{
    [TestMethod]
    public void GeneratedCodeShouldBeParsable()
    {
        for (var i = 0; i < 500; i++)
        {
            var port = Random.Shared.Next(100, 65536);

            var generated = LobbyInfoProvider.Generate(port);
            Assert.AreEqual(port, generated.Port);
            Assert.AreEqual(29, generated.OriginalCode.Length);

            var parsed = LobbyInfoProvider.ParseCode(generated.OriginalCode);
            Assert.IsNotNull(parsed);
            Assert.AreEqual(generated.OriginalCode, parsed.OriginalCode);
            Assert.AreEqual(generated.NetworkName, parsed.NetworkName);
            Assert.AreEqual(generated.NetworkSecret, parsed.NetworkSecret);
            Assert.AreEqual(port, parsed.Port);
            Assert.AreEqual(LobbyInfoProvider.HostIp, parsed.Ip);
        }
    }

    [TestMethod]
    public void CodeShouldBeCaseInsensitive()
    {
        var generated = LobbyInfoProvider.Generate(25565);
        var parsed = LobbyInfoProvider.ParseCode(generated.OriginalCode.ToLower());

        Assert.IsNotNull(parsed);
        Assert.AreEqual(generated.NetworkName, parsed.NetworkName);
    }

    [TestMethod]
    public void InvalidCodeShouldBeRejected()
    {
        Assert.IsNull(LobbyInfoProvider.ParseCode(""));
        Assert.IsNull(LobbyInfoProvider.ParseCode("ABCDE"));
        Assert.IsNull(LobbyInfoProvider.ParseCode("U/ABCD-EFGH-IJKL-MNOP"));
    }
}

using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using OperationGuard.Core.Internal;
using OperationGuard.Core.Models;
using Xunit;

namespace OperationGuard.Core.Tests;

public sealed class RelationalNullParameterTests
{
    [Fact]
    public void Fixed_width_identity_hash_is_bound_as_fixed_length_ansi_text()
    {
        using var command = new SqlCommand();
        var addIdentityHash = typeof(RelationalOperationStore).GetMethod(
            "AddIdentityHash",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Identity-hash parameter binding method was not found.");

        addIdentityHash.Invoke(
            null,
            [command, new OperationIdentity("tenant", "Payments.Create", "key")]);

        var parameter = command.Parameters["@identityHash"];
        Assert.Equal(DbType.AnsiStringFixedLength, parameter.DbType);
        Assert.Equal(64, parameter.Size);
        Assert.IsType<string>(parameter.Value);
    }

    [Fact]
    public void Null_transactional_completion_response_uses_explicit_database_types()
    {
        using var command = new SqlCommand();

        RelationalOperationStore.AddResponseParameters(
            command,
            response: null,
            replayBodyAvailable: false,
            responseDigest: null);

        AssertParameter(command, "@statusCode", DbType.Int32, expectedSize: 0);
        AssertParameter(command, "@responseHeaders", DbType.String, expectedSize: -1);
        AssertParameter(command, "@responseBody", DbType.Binary, expectedSize: -1);
        AssertParameter(command, "@replayBodyAvailable", DbType.Boolean, expectedSize: 0, expectedValue: false);
        AssertParameter(command, "@responseDigest", DbType.String, expectedSize: 128);
    }

    private static void AssertParameter(
        SqlCommand command,
        string name,
        DbType expectedType,
        int expectedSize,
        object? expectedValue = null)
    {
        var parameter = command.Parameters[name];
        Assert.Equal(expectedType, parameter.DbType);
        Assert.Equal(expectedSize, parameter.Size);
        Assert.Equal(expectedValue ?? DBNull.Value, parameter.Value);
    }
}

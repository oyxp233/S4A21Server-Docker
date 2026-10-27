using System;
using System.IO;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Auction.Storage;

internal static class AuctionDatabase
{
    internal const int CurrentVersion = 1;
    internal static string DefaultPath => Environment.GetEnvironmentVariable("AUCTION_DATABASE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "Data", "auction.db");
    internal static IGameDatabase Open(string path)
        => FeatureDatabaseBootstrap.Open(path, "a21-auction-v1", Schema);
    private const string Schema = @"
CREATE TABLE auction_operation (
    sequence_id INTEGER PRIMARY KEY AUTOINCREMENT,
    operation_id TEXT NOT NULL UNIQUE,
    resource_key TEXT NOT NULL CHECK(length(resource_key) > 0),
    action INTEGER NOT NULL CHECK(action BETWEEN 1 AND 7),
    character_id INTEGER NOT NULL CHECK(character_id > 0),
    account_id INTEGER NOT NULL CHECK(account_id > 0),
    expected_version INTEGER NOT NULL CHECK(expected_version >= 0),
    created_at_unix INTEGER NOT NULL CHECK(created_at_unix > 0),
    intent_json TEXT NOT NULL,
    intent_hash TEXT NOT NULL CHECK(length(intent_hash) = 64),
    state INTEGER NOT NULL DEFAULT 0 CHECK(state BETWEEN 0 AND 3),
    result_json TEXT NOT NULL DEFAULT '{}',
    resolution_reason TEXT NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX idx_auction_operation_reservation
    ON auction_operation(resource_key) WHERE state IN (0,3);
CREATE INDEX idx_auction_operation_pending
    ON auction_operation(state,sequence_id);
CREATE INDEX idx_auction_operation_character
    ON auction_operation(character_id,state);
CREATE TABLE auction_market_sequence (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    next_id INTEGER NOT NULL CHECK(next_id>0)
);
INSERT INTO auction_market_sequence VALUES(1,1);
CREATE TABLE auction_order (
    order_id INTEGER PRIMARY KEY CHECK(order_id>0),
    version INTEGER NOT NULL CHECK(version>=0),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4),
    seller_character_id INTEGER NOT NULL CHECK(seller_character_id>0),
    seller_account_id INTEGER NOT NULL CHECK(seller_account_id>0),
    bidder_character_id INTEGER,
    quota_key TEXT NOT NULL,
    expires_at_unix INTEGER NOT NULL,
    pending_operation_id TEXT REFERENCES auction_operation(operation_id),
    snapshot_json TEXT NOT NULL,
    snapshot_hash TEXT NOT NULL CHECK(length(snapshot_hash)=64)
);
CREATE INDEX idx_auction_order_quota ON auction_order(quota_key,state);
CREATE INDEX idx_auction_order_expiry ON auction_order(state,expires_at_unix,order_id);
CREATE INDEX idx_auction_order_seller ON auction_order(seller_character_id,state);
CREATE INDEX idx_auction_order_bidder ON auction_order(bidder_character_id,state);
CREATE TABLE auction_order_delivery (
    operation_id TEXT NOT NULL REFERENCES auction_operation(operation_id),
    leg_key TEXT NOT NULL,
    order_id INTEGER NOT NULL REFERENCES auction_order(order_id),
    recipient_character_id INTEGER NOT NULL,
    public_message_id INTEGER NOT NULL CHECK(public_message_id>0),
    leg_json TEXT NOT NULL,
    PRIMARY KEY(operation_id,leg_key)
);";
}

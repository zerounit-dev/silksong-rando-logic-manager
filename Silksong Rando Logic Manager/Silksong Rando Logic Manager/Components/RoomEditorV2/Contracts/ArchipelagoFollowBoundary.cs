namespace Silksong_Rando_Logic_Manager.Components.RoomEditorV2.Contracts;

// Preflight-only native editor facts. Saving remains the ordinary blur handler's
// responsibility; this record is never a save intent or completion receipt.
public sealed record ArchipelagoFollowBoundary(string Kind, string Client, string Field, string Value);

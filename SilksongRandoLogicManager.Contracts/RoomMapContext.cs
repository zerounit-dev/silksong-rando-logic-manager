using Silksong_Rando_Logic_Manager.Services;

namespace SilksongRandoLogicManager.Contracts;

public enum RoomMapContextState { NoImportedMap, UnusableGeometry, Ready }
public sealed record RoomMapContextView(Guid RoomId, RoomMapContextState State, AreaMapSurfaceView? Surface);

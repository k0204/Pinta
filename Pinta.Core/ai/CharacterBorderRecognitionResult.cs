namespace Pinta.Core.AI;

public sealed record CharacterBorderRecognitionResult (
	byte[] PartPng,
	byte[] MaskPng);

namespace VolumetricExplosionFX.Core;

public static class BodyLook
{
	public static Vec3 Dust(string body)
	{
		return body switch
		{
			"Kerbin" => new Vec3(0.62, 0.55, 0.43), 
			"Mun" => new Vec3(0.6, 0.6, 0.58), 
			"Minmus" => new Vec3(0.76, 0.87, 0.83), 
			"Duna" => new Vec3(0.68, 0.39, 0.25), 
			"Ike" => new Vec3(0.5, 0.5, 0.5), 
			"Eve" => new Vec3(0.47, 0.37, 0.53), 
			"Gilly" => new Vec3(0.56, 0.46, 0.4), 
			"Moho" => new Vec3(0.5, 0.43, 0.38), 
			"Dres" => new Vec3(0.58, 0.56, 0.53), 
			"Laythe" => new Vec3(0.66, 0.6, 0.5), 
			"Vall" => new Vec3(0.78, 0.83, 0.88), 
			"Tylo" => new Vec3(0.72, 0.7, 0.64), 
			"Bop" => new Vec3(0.48, 0.41, 0.35), 
			"Pol" => new Vec3(0.78, 0.72, 0.48), 
			"Eeloo" => new Vec3(0.85, 0.86, 0.85), 
			_ => new Vec3(0.62, 0.56, 0.46), 
		};
	}
}

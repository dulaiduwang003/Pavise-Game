using System;

namespace PaviseApp
{
	internal struct Motion
	{
		public float Value;

		public float Target;

		public float Speed;

		public void Set(float v)
		{
			Value = v;
			Target = v;
		}

		public void To(float t)
		{
			Target = t;
		}

		public bool Step()
		{
			if (Speed <= 0f)
			{
				Speed = 0.25f;
			}
			if (UiClock.Frozen)
			{
				if (Value == Target)
				{
					return false;
				}
				Value = Target;
				return true;
			}
			float num = Target - Value;
			if (num < 0.0015f && num > -0.0015f)
			{
				if (Value != Target)
				{
					Value = Target;
					return true;
				}
				return false;
			}
			Value += num * StepFraction(Speed, UiClock.DeltaScale);
			return true;
		}

		internal static float StepFraction(float speed, float deltaScale)
		{
			if (speed >= 1f)
			{
				return 1f;
			}
			if (deltaScale <= 0f)
			{
				return 0f;
			}
			if (deltaScale == 1f)
			{
				return speed;
			}
			double num = Math.Pow(1.0 - (double)speed, deltaScale);
			float num2 = (float)(1.0 - num);
			if (!(num2 > 1f))
			{
				return num2;
			}
			return 1f;
		}
	}
}

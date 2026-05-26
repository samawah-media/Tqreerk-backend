namespace Taqreerk.Application.DTOs.Auth;

public record SendVerificationEmailRequest(string Email);
public record ConfirmEmailRequest(string Token);

public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);

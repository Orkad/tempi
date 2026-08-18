using Tempi;

// Squelette : la commande racine et les onze sous-commandes arrivent avec le
// portage de cli.py. Seul « --version » est câblé, pour que la chaîne de
// compilation et la CI aient quelque chose de vérifiable dès maintenant.
if (args is ["--version"])
{
    Console.WriteLine($"tempi {TempiVersion.Value}");
    return 0;
}

Console.Error.WriteLine("tempi : portage .NET en cours, aucune commande n'est encore câblée.");
return 2;

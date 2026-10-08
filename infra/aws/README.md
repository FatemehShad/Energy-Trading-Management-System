# AWS deployment

This Terraform module deploys ECR, ECS Fargate, HTTPS ALB, task IAM roles and CloudWatch logs into an **existing VPC**. Supply existing private PostgreSQL/RDS and Kafka brokers (for example MSK with plaintext enabled on a private network). It does not create a database, Kafka cluster, VPC, certificate, DNS record, or GitHub OIDC role. Those depend on your AWS account and cost requirements. No deployment has been performed or validated against AWS.

Use two public ALB subnets and private task subnets with NAT or suitable ECR/CloudWatch/Secrets Manager endpoints. Provide an ACM certificate in the same region and set DNS for its hostname to the ALB. Restrict `allowed_client_cidrs`. Database and Kafka security groups receive ingress from the task security group. This sample publisher supports private plaintext Kafka on port 9092; do not point it at an MSK TLS/IAM endpoint without implementing the corresponding producer authentication configuration.

Create two Secrets Manager secrets outside Terraform: one containing the complete Npgsql connection string (use `SSL Mode=VerifyFull` for RDS with the appropriate CA trust installed) and one containing a strong random API key. Supply their ARNs, never their contents. Customer-managed KMS keys additionally require `kms:Decrypt` in the execution role and key policy. Application secrets are injected directly by ECS.

## First deployment

1. Configure AWS credentials and Terraform >= 1.6. Store state in a protected remote backend for real use. Fill an ignored `terraform.tfvars` with the variables declared in `main.tf`; `image` must be a valid application image URI.
2. Run `terraform init`, then `terraform apply -target=aws_ecr_repository.api` to bootstrap the registry only. Build and push a unique tag to that ECR registry with Docker and `aws ecr get-login-password`. Set `image` to the pushed image URI.
3. Run `terraform plan`, review costs and resources, then `terraform apply`. The service deliberately starts at zero tasks so migrations can run first.
4. Create a GitHub `production` environment, preferably requiring deployment review, with variables `AWS_REGION`, `AWS_DEPLOY_ROLE_ARN`, `ECR_REPOSITORY` (full repository URI), `ECS_CLUSTER=energy-trading`, and `ECS_SERVICE=energy-trading`.
5. Configure GitHub OIDC role trust scoped to this repository's `production` environment. Grant that deployment role ECR push to this repository, ECS describe/register/run/update/wait-related read permissions, and `iam:PassRole` only for the execution and task roles. Never grant a general administrator role for deployments.
6. Trigger **Deploy to AWS**. It runs unit tests, builds/pushes an immutable image, registers a task definition, runs a one-shot migration task, requires exit code zero, updates the service, and checks that the new deployment completed. Existing deployments stay running if migration fails. Check the HTTPS `/health/ready` endpoint and make an authenticated trade request after deployment; inspect CloudWatch logs and Kafka events.

Terraform ignores task definition/desired count changes on the service because CI owns deployments. Changing task configuration in Terraform requires reconciling the active task definition before the next deployment. Schema changes must remain compatible with old tasks during rolling deployment. The current initial migration is suitable for the initial release; review future migrations and backups explicitly.

This is deployment scaffolding, not evidence of a live AWS deployment. AWS access, external services and configuration are required. The project should gain OIDC-based application authorization and Kafka transport authentication before broader production exposure.

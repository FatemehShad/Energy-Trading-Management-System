terraform {
  required_version = ">= 1.6"
  required_providers { aws = { source = "hashicorp/aws", version = "~> 5.0" } }
}
provider "aws" { region = var.region }
variable "region" { type = string }
variable "vpc_id" { type = string }
variable "public_subnet_ids" { type = list(string) }
variable "private_subnet_ids" { type = list(string) }
variable "certificate_arn" { type = string }
variable "allowed_client_cidrs" { type = list(string) }
variable "image" { type = string }
variable "connection_string_secret_arn" { type = string }
variable "api_key_secret_arn" { type = string }
variable "kafka_bootstrap_servers" { type = string }
variable "database_security_group_id" { type = string }
variable "kafka_security_group_id" { type = string }
resource "aws_ecr_repository" "api" {
  name                 = "energy-trading"
  image_tag_mutability = "IMMUTABLE"
  image_scanning_configuration { scan_on_push = true }
}
resource "aws_cloudwatch_log_group" "api" {
  name              = "/ecs/energy-trading"
  retention_in_days = 30
}
resource "aws_ecs_cluster" "main" { name = "energy-trading" }
resource "aws_iam_role" "execution" {
  name               = "energy-trading-execution"
  assume_role_policy = jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Principal = { Service = "ecs-tasks.amazonaws.com" }, Action = "sts:AssumeRole" }] })
}
resource "aws_iam_role_policy_attachment" "execution" {
  role       = aws_iam_role.execution.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
}
resource "aws_iam_role_policy" "secrets" {
  role   = aws_iam_role.execution.id
  policy = jsonencode({ Version = "2012-10-17", Statement = [{ Effect = "Allow", Action = ["secretsmanager:GetSecretValue"], Resource = [var.connection_string_secret_arn, var.api_key_secret_arn] }] })
}
resource "aws_iam_role" "task" {
  name               = "energy-trading-task"
  assume_role_policy = aws_iam_role.execution.assume_role_policy
}
resource "aws_security_group" "alb" {
  name   = "energy-trading-alb"
  vpc_id = var.vpc_id
  ingress {
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = var.allowed_client_cidrs
  }
  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}
resource "aws_security_group" "api" {
  name   = "energy-trading-api"
  vpc_id = var.vpc_id
  ingress {
    from_port       = 8080
    to_port         = 8080
    protocol        = "tcp"
    security_groups = [aws_security_group.alb.id]
  }
  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }
}
resource "aws_vpc_security_group_ingress_rule" "postgres" {
  security_group_id            = var.database_security_group_id
  referenced_security_group_id = aws_security_group.api.id
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
}
resource "aws_vpc_security_group_ingress_rule" "kafka" {
  security_group_id            = var.kafka_security_group_id
  referenced_security_group_id = aws_security_group.api.id
  from_port                    = 9092
  to_port                      = 9092
  ip_protocol                  = "tcp"
}
resource "aws_lb" "api" {
  name               = "energy-trading"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = var.public_subnet_ids
}
resource "aws_lb_target_group" "api" {
  name        = "energy-trading"
  port        = 8080
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = var.vpc_id
  health_check { path = "/health/ready" }
}
resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.api.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.certificate_arn
  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}
resource "aws_ecs_task_definition" "api" {
  family                   = "energy-trading"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions = jsonencode([{
    name             = "api", image = var.image, essential = true,
    portMappings     = [{ containerPort = 8080, protocol = "tcp" }],
    environment      = [{ name = "ASPNETCORE_ENVIRONMENT", value = "Production" }, { name = "Kafka__BootstrapServers", value = var.kafka_bootstrap_servers }],
    secrets          = [{ name = "ConnectionStrings__Trading", valueFrom = var.connection_string_secret_arn }, { name = "ApiKey", valueFrom = var.api_key_secret_arn }],
    logConfiguration = { logDriver = "awslogs", options = { "awslogs-group" = aws_cloudwatch_log_group.api.name, "awslogs-region" = var.region, "awslogs-stream-prefix" = "api" } }
  }])
}
resource "aws_ecs_service" "api" {
  name            = "energy-trading"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = 0
  launch_type     = "FARGATE"
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }
  network_configuration {
    subnets          = var.private_subnet_ids
    security_groups  = [aws_security_group.api.id]
    assign_public_ip = false
  }
  load_balancer {
    target_group_arn = aws_lb_target_group.api.arn
    container_name   = "api"
    container_port   = 8080
  }
  depends_on = [aws_lb_listener.https, aws_iam_role_policy.secrets, aws_iam_role_policy_attachment.execution]
  lifecycle { ignore_changes = [task_definition, desired_count] }
}
output "ecr_repository" { value = aws_ecr_repository.api.repository_url }
output "cluster" { value = aws_ecs_cluster.main.name }
output "service" { value = aws_ecs_service.api.name }
output "load_balancer_dns" { value = aws_lb.api.dns_name }
